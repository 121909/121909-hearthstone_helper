using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using DiscardAdvisor.Rules;
using DiscardAdvisor.Rules.Model;

namespace DiscardAdvisor.Search;

public sealed record RandomSamplingOptions(
    int Seed = 0x5EED,
    int ExactOutcomeLimit = 64,
    int MonteCarloSamples = 48)
{
    public void Validate()
    {
        if (ExactOutcomeLimit < 1)
            throw new ArgumentOutOfRangeException(nameof(ExactOutcomeLimit));
        if (MonteCarloSamples < 1)
            throw new ArgumentOutOfRangeException(nameof(MonteCarloSamples));
    }
}

public sealed record RandomOneCostMinion(
    string CardId,
    int Attack,
    int Health,
    bool Taunt = false,
    bool Rush = false,
    bool Charge = false,
    bool Stealth = false,
    bool DivineShield = false,
    bool Poisonous = false,
    bool Lifesteal = false,
    bool HasUnmodeledEffects = false);

public interface IRandomOneCostMinionPool
{
    string Version { get; }

    IReadOnlyList<RandomOneCostMinion> Candidates { get; }
}

public sealed class StaticRandomOneCostMinionPool : IRandomOneCostMinionPool
{
    public StaticRandomOneCostMinionPool(string version, IEnumerable<RandomOneCostMinion> candidates)
    {
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("A pool version is required.", nameof(version));
        if (candidates is null)
            throw new ArgumentNullException(nameof(candidates));

        Version = version;
        Candidates = candidates
            .OrderBy(candidate => candidate.CardId, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    public string Version { get; }

    public IReadOnlyList<RandomOneCostMinion> Candidates { get; }
}

public sealed record RandomOutcome(
    RuleGameState State,
    ImmutableArray<RuleEvent> Events,
    double Probability,
    bool UsesMonteCarlo);

public sealed class RandomOutcomeSampler
{
    private const string RandomDamagePending = "random_damage_pending";
    private const string RandomSummonPending = "random_one_cost_summon_pending";
    private readonly IRandomOneCostMinionPool _oneCostMinions;
    private readonly DiscardWarlockRuleEngine _continuations = new();

    public RandomOutcomeSampler()
        : this(new StaticRandomOneCostMinionPool("unavailable", Array.Empty<RandomOneCostMinion>()))
    {
    }

    public RandomOutcomeSampler(IRandomOneCostMinionPool oneCostMinions)
    {
        _oneCostMinions = oneCostMinions ?? throw new ArgumentNullException(nameof(oneCostMinions));
    }

    public ImmutableArray<RandomOutcome> Resolve(
        TransitionResult transition,
        RandomSamplingOptions options,
        Random random)
    {
        if (random is null)
            throw new ArgumentNullException(nameof(random));
        return Resolve(transition, options, new SystemRandomSource(random));
    }

    public ImmutableArray<RandomOutcome> Resolve(
        TransitionResult transition,
        RandomSamplingOptions options,
        IRandomSource random)
        => Resolve(transition, options, random, null);

    public ImmutableArray<RandomOutcome> Resolve(
        TransitionResult transition,
        RandomSamplingOptions options,
        IRandomSource random,
        Func<bool>? shouldStop)
    {
        if (transition is null)
            throw new ArgumentNullException(nameof(transition));
        if (options is null)
            throw new ArgumentNullException(nameof(options));
        if (random is null)
            throw new ArgumentNullException(nameof(random));
        options.Validate();

        var roots = transition.Branches.IsEmpty
            ? ImmutableArray.Create(new RandomOutcome(transition.State, transition.Events, 1d, false))
            : transition.Branches.Select(branch => new RandomOutcome(
                    branch.State,
                    branch.Events,
                    branch.Probability,
                    false))
                .ToImmutableArray();
        var resolved = new List<RandomOutcome>();
        var exactLimitPerRoot = Math.Max(1, options.ExactOutcomeLimit / Math.Max(1, roots.Length));

        foreach (var root in roots)
        {
            if (shouldStop?.Invoke() == true)
                break;
            if (TryResolveExactly(root, exactLimitPerRoot, out var exact))
            {
                resolved.AddRange(exact);
                continue;
            }

            var sampleCount = Math.Max(1, (int)Math.Round(options.MonteCarloSamples * root.Probability));
            var sampleProbability = root.Probability / sampleCount;
            for (var index = 0; index < sampleCount; index++)
            {
                if (shouldStop?.Invoke() == true)
                    break;
                var sampled = ResolveSample(root with { Probability = sampleProbability }, random);
                resolved.Add(sampled);
            }
        }

        return Consolidate(resolved)
            .OrderBy(outcome => RuleStateKey.Calculate(outcome.State), StringComparer.Ordinal)
            .ToImmutableArray();
    }

    public ImmutableArray<RandomOutcome> Resolve(
        TransitionResult transition,
        RandomSamplingOptions? options = null)
    {
        options ??= new RandomSamplingOptions();
        return Resolve(transition, options, new SeededRandomSource(options.Seed));
    }

    private bool TryResolveExactly(
        RandomOutcome root,
        int outcomeLimit,
        out ImmutableArray<RandomOutcome> resolved)
    {
        var current = ImmutableArray.Create(root);
        while (true)
        {
            var expanded = new List<RandomOutcome>();
            var foundPending = false;
            foreach (var outcome in current)
            {
                var pendingIndex = FindPendingIndex(outcome.Events);
                if (pendingIndex < 0)
                {
                    expanded.Add(outcome);
                    continue;
                }

                foundPending = true;
                if (!CanExpandExactly(outcome, outcome.Events[pendingIndex], outcomeLimit - expanded.Count))
                {
                    resolved = ImmutableArray<RandomOutcome>.Empty;
                    return false;
                }
                expanded.AddRange(ExpandPendingExactly(outcome, pendingIndex));
                if (expanded.Count > outcomeLimit)
                {
                    resolved = ImmutableArray<RandomOutcome>.Empty;
                    return false;
                }
            }

            if (!foundPending)
            {
                resolved = current;
                return true;
            }

            current = Consolidate(expanded);
            if (current.Length > outcomeLimit)
            {
                resolved = ImmutableArray<RandomOutcome>.Empty;
                return false;
            }
        }
    }

    private bool CanExpandExactly(RandomOutcome outcome, RuleEvent pending, int remainingLimit)
    {
        if (remainingLimit < 1)
            return false;

        var choices = pending.Type switch
        {
            RandomDamagePending => RandomDamageTargets(
                outcome.State,
                FindSourceSide(outcome.State, pending.SourceEntityId)).Length,
            RandomSummonPending => _oneCostMinions.Candidates.Count,
            DiscardWarlockRuleEngine.RandomDrawPending or
                DiscardWarlockRuleEngine.RandomTemporaryDrawPending or
                DiscardWarlockRuleEngine.RandomBoundDrawPending => outcome.State
                    .Player((PlayerSide)(pending.TargetEntityId ?? 0)).Deck.Length,
            _ => 1
        };
        if (choices <= 1 || pending.Amount <= 0)
            return true;

        var upperBound = 1L;
        for (var index = 0; index < pending.Amount; index++)
        {
            upperBound *= choices;
            if (upperBound > remainingLimit)
                return false;
        }

        return true;
    }

    private IEnumerable<RandomOutcome> ExpandPendingExactly(RandomOutcome outcome, int pendingIndex)
    {
        var pending = outcome.Events[pendingIndex];
        return pending.Type switch
        {
            RandomDamagePending => ExpandRandomDamageExactly(outcome, pendingIndex, pending),
            RandomSummonPending => ExpandRandomSummonsExactly(outcome, pendingIndex, pending),
            DiscardWarlockRuleEngine.RandomDrawPending or
                DiscardWarlockRuleEngine.RandomTemporaryDrawPending or
                DiscardWarlockRuleEngine.RandomBoundDrawPending => ExpandRandomDrawExactly(outcome, pendingIndex, pending),
            DiscardWarlockRuleEngine.ContinueWickedWhispersPending or
                DiscardWarlockRuleEngine.ContinueChamberDrawPending or
                DiscardWarlockRuleEngine.ContinueEndTurnPending or
                DiscardWarlockRuleEngine.ContinueLifeTapDamagePending => ExpandContinuation(outcome, pendingIndex, pending),
            _ => new[] { outcome }
        };
    }

    private static IEnumerable<RandomOutcome> ExpandRandomDamageExactly(
        RandomOutcome outcome,
        int pendingIndex,
        RuleEvent pending)
    {
        var partials = ImmutableArray.Create(new PartialOutcome(
            outcome.State,
            ImmutableArray<RuleEvent>.Empty,
            outcome.Probability));
        var sourceSide = FindSourceSide(outcome.State, pending.SourceEntityId);

        for (var missile = 0; missile < Math.Max(0, pending.Amount); missile++)
        {
            var next = new List<PartialOutcome>();
            foreach (var partial in partials)
            {
                if (IsTerminal(partial.State))
                {
                    next.Add(partial);
                    continue;
                }
                var targets = RandomDamageTargets(partial.State, sourceSide);
                if (targets.IsEmpty)
                {
                    next.Add(partial with
                    {
                        Events = partial.Events.Add(new RuleEvent(
                            "random_damage_no_target",
                            pending.SourceEntityId,
                            null,
                            0,
                            pending.CardId))
                    });
                    continue;
                }

                var targetProbability = 1d / targets.Length;
                foreach (var targetEntityId in targets)
                {
                    var damage = DealOneDamage(partial.State, sourceSide, pending.SourceEntityId, targetEntityId);
                    next.Add(new PartialOutcome(
                        damage.State,
                        partial.Events.AddRange(damage.Events),
                        partial.Probability * targetProbability));
                }
            }

            partials = ConsolidatePartials(next);
        }

        if (pending.Amount <= 0)
        {
            partials = partials.Select(partial => partial with
            {
                Events = partial.Events.Add(new RuleEvent(
                    "random_damage_resolved",
                    pending.SourceEntityId,
                    null,
                    0,
                    pending.CardId))
            }).ToImmutableArray();
        }

        return partials.Select(partial => new RandomOutcome(
            partial.State,
            ReplacePending(outcome.Events, pendingIndex, partial.Events),
            partial.Probability,
            outcome.UsesMonteCarlo));
    }

    private IEnumerable<RandomOutcome> ExpandRandomSummonsExactly(
        RandomOutcome outcome,
        int pendingIndex,
        RuleEvent pending)
    {
        if (_oneCostMinions.Candidates.Count == 0)
            return new[] { ResolveUnavailablePool(outcome, pendingIndex, pending) };

        var partials = ImmutableArray.Create(new PartialOutcome(
            outcome.State,
            ImmutableArray<RuleEvent>.Empty,
            outcome.Probability));
        var sourceSide = FindSourceSide(outcome.State, pending.SourceEntityId);

        for (var summon = 0; summon < Math.Max(0, pending.Amount); summon++)
        {
            var next = new List<PartialOutcome>();
            foreach (var partial in partials)
            {
                var candidateProbability = 1d / _oneCostMinions.Candidates.Count;
                foreach (var candidate in _oneCostMinions.Candidates)
                {
                    var summoned = Summon(partial.State, sourceSide, pending.SourceEntityId, candidate);
                    next.Add(new PartialOutcome(
                        summoned.State,
                        partial.Events.AddRange(summoned.Events),
                        partial.Probability * candidateProbability));
                }
            }

            partials = ConsolidatePartials(next);
        }

        return partials.Select(partial => new RandomOutcome(
            partial.State,
            ReplacePending(outcome.Events, pendingIndex, partial.Events),
            partial.Probability,
            outcome.UsesMonteCarlo));
    }

    private IEnumerable<RandomOutcome> ExpandContinuation(
        RandomOutcome outcome,
        int pendingIndex,
        RuleEvent pending)
    {
        var continuation = _continuations.ResolvePendingContinuation(outcome.State, pending);
        if (!continuation.IsLegal)
            return new[] { outcome };
        return new[]
        {
            outcome with
            {
                State = continuation.State,
                Events = ReplacePending(outcome.Events, pendingIndex, continuation.Events)
            }
        };
    }

    private IEnumerable<RandomOutcome> ExpandRandomDrawExactly(
        RandomOutcome outcome,
        int pendingIndex,
        RuleEvent pending)
    {
        var side = (PlayerSide)(pending.TargetEntityId ?? 0);
        var deck = outcome.State.Player(side).Deck;
        if (deck.IsEmpty)
            return new[] { outcome };
        var probability = 1d / deck.Length;
        return deck.Select(card =>
        {
            var draw = _continuations.ResolveRandomDraw(outcome.State, pending, card.EntityId);
            return outcome with
            {
                State = draw.State,
                Events = ReplacePending(outcome.Events, pendingIndex, draw.Events),
                Probability = outcome.Probability * probability
            };
        });
    }

    private RandomOutcome ResolveSample(RandomOutcome root, IRandomSource random)
    {
        var current = root;
        for (var guard = 0; guard < 32; guard++)
        {
            var pendingIndex = FindPendingIndex(current.Events);
            if (pendingIndex < 0)
                return current;

            var pending = current.Events[pendingIndex];
            current = pending.Type switch
            {
                RandomDamagePending => SampleRandomDamage(current, pendingIndex, pending, random),
                RandomSummonPending => SampleRandomSummons(current, pendingIndex, pending, random),
                DiscardWarlockRuleEngine.RandomDrawPending or
                    DiscardWarlockRuleEngine.RandomTemporaryDrawPending or
                    DiscardWarlockRuleEngine.RandomBoundDrawPending => SampleRandomDraw(current, pendingIndex, pending, random),
                DiscardWarlockRuleEngine.ContinueWickedWhispersPending or
                    DiscardWarlockRuleEngine.ContinueChamberDrawPending or
                    DiscardWarlockRuleEngine.ContinueEndTurnPending or
                    DiscardWarlockRuleEngine.ContinueLifeTapDamagePending => ResolveContinuation(current, pendingIndex, pending),
                _ => current
            };
        }

        return current;
    }

    private static RandomOutcome SampleRandomDamage(
        RandomOutcome outcome,
        int pendingIndex,
        RuleEvent pending,
        IRandomSource random)
    {
        var state = outcome.State;
        var events = ImmutableArray<RuleEvent>.Empty;
        var sourceSide = FindSourceSide(state, pending.SourceEntityId);
        for (var missile = 0; missile < Math.Max(0, pending.Amount); missile++)
        {
            if (IsTerminal(state))
                break;
            var targets = RandomDamageTargets(state, sourceSide);
            if (targets.IsEmpty)
            {
                events = events.Add(new RuleEvent(
                    "random_damage_no_target",
                    pending.SourceEntityId,
                    null,
                    0,
                    pending.CardId));
                continue;
            }

            var damage = DealOneDamage(state, sourceSide, pending.SourceEntityId, targets[random.Next(targets.Length)]);
            state = damage.State;
            events = events.AddRange(damage.Events);
        }

        return outcome with
        {
            State = state,
            Events = ReplacePending(outcome.Events, pendingIndex, events),
            UsesMonteCarlo = true
        };
    }

    private RandomOutcome ResolveContinuation(RandomOutcome outcome, int pendingIndex, RuleEvent pending)
    {
        var continuation = _continuations.ResolvePendingContinuation(outcome.State, pending);
        if (!continuation.IsLegal)
            return outcome;
        return outcome with
        {
            State = continuation.State,
            Events = ReplacePending(outcome.Events, pendingIndex, continuation.Events)
        };
    }

    private RandomOutcome SampleRandomDraw(
        RandomOutcome outcome,
        int pendingIndex,
        RuleEvent pending,
        IRandomSource random)
    {
        var side = (PlayerSide)(pending.TargetEntityId ?? 0);
        var deck = outcome.State.Player(side).Deck;
        if (deck.IsEmpty)
            return outcome;
        var selected = deck[random.Next(deck.Length)];
        var draw = _continuations.ResolveRandomDraw(outcome.State, pending, selected.EntityId);
        return outcome with
        {
            State = draw.State,
            Events = ReplacePending(outcome.Events, pendingIndex, draw.Events),
            UsesMonteCarlo = true
        };
    }

    private RandomOutcome SampleRandomSummons(
        RandomOutcome outcome,
        int pendingIndex,
        RuleEvent pending,
        IRandomSource random)
    {
        if (_oneCostMinions.Candidates.Count == 0)
            return ResolveUnavailablePool(outcome, pendingIndex, pending);

        var state = outcome.State;
        var events = ImmutableArray<RuleEvent>.Empty;
        var sourceSide = FindSourceSide(state, pending.SourceEntityId);
        for (var summon = 0; summon < Math.Max(0, pending.Amount); summon++)
        {
            var candidate = _oneCostMinions.Candidates[random.Next(_oneCostMinions.Candidates.Count)];
            var summoned = Summon(state, sourceSide, pending.SourceEntityId, candidate);
            state = summoned.State;
            events = events.AddRange(summoned.Events);
        }

        return outcome with
        {
            State = state,
            Events = ReplacePending(outcome.Events, pendingIndex, events),
            UsesMonteCarlo = true
        };
    }

    private RandomOutcome ResolveUnavailablePool(RandomOutcome outcome, int pendingIndex, RuleEvent pending)
    {
        var unresolved = new RuleEvent(
            "random_one_cost_summon_unresolved",
            pending.SourceEntityId,
            null,
            pending.Amount,
            _oneCostMinions.Version);
        return outcome with { Events = ReplacePending(outcome.Events, pendingIndex, ImmutableArray.Create(unresolved)) };
    }

    private static DamageResult DealOneDamage(
        RuleGameState state,
        PlayerSide sourceSide,
        int? sourceEntityId,
        int targetEntityId)
    {
        var targetSide = RuleGameState.Other(sourceSide);
        var targetPlayer = state.Player(targetSide);
        var events = ImmutableArray<RuleEvent>.Empty;
        if (targetPlayer.Hero.EntityId == targetEntityId)
        {
            var damage = RuleDamage.Apply(targetPlayer.Hero, 1);
            events = events.Add(new RuleEvent("damage", sourceEntityId, targetEntityId, damage.DamageApplied));
            return new DamageResult(state.WithPlayer(targetSide, targetPlayer with { Hero = damage.Hero }), events);
        }

        var minion = targetPlayer.Board.FirstOrDefault(candidate => candidate.EntityId == targetEntityId);
        if (minion is null)
            return new DamageResult(state, events);

        var damageToMinion = RuleDamage.Apply(minion, 1);
        var damaged = damageToMinion.Minion;
        if (damageToMinion.DivineShieldLost)
            events = events.Add(new RuleEvent("divine_shield_lost", sourceEntityId, targetEntityId));
        events = events.Add(new RuleEvent("damage", sourceEntityId, targetEntityId, damageToMinion.DamageApplied));
        if (damaged.Health > 0)
        {
            targetPlayer = targetPlayer with { Board = targetPlayer.Board.Replace(minion, damaged) };
            return new DamageResult(state.WithPlayer(targetSide, targetPlayer), events);
        }

        targetPlayer = (targetPlayer with
        {
            Board = targetPlayer.Board.Remove(minion),
            Graveyard = targetPlayer.Graveyard.Add(new ZoneCardState(minion.EntityId, minion.CardId))
        }).NormalizePositions();
        events = events.Add(new RuleEvent("death", minion.EntityId, null, 0, minion.CardId));
        return new DamageResult(state.WithPlayer(targetSide, targetPlayer), events);
    }

    private static SummonResult Summon(
        RuleGameState state,
        PlayerSide side,
        int? sourceEntityId,
        RandomOneCostMinion candidate)
    {
        var player = state.Player(side);
        if (player.BoardCount >= CommonRuleEngine.MaximumBoardSize)
        {
            return new SummonResult(
                state,
                ImmutableArray.Create(new RuleEvent(
                    "summon_failed_board_full",
                    sourceEntityId,
                    null,
                    0,
                    candidate.CardId)));
        }

        state = state.AllocateEntity(out var entityId);
        player = state.Player(side);
        var minion = new MinionState(
            entityId,
            candidate.CardId,
            player.BoardCount + 1,
            Math.Max(0, candidate.Attack),
            Math.Max(1, candidate.Health),
            Math.Max(1, candidate.Health),
            Taunt: candidate.Taunt,
            Rush: candidate.Rush,
            Charge: candidate.Charge,
            Stealth: candidate.Stealth,
            SummonedThisTurn: true,
            DivineShield: candidate.DivineShield,
            Poisonous: candidate.Poisonous,
            Lifesteal: candidate.Lifesteal);
        player = player with { Board = player.Board.Add(minion) };
        var events = ImmutableArray.Create(new RuleEvent("summon", sourceEntityId, entityId, 0, candidate.CardId));
        if (candidate.HasUnmodeledEffects)
        {
            events = events.Add(new RuleEvent(
                "random_summon_effect_unmodeled",
                entityId,
                null,
                0,
                candidate.CardId));
        }

        return new SummonResult(state.WithPlayer(side, player), events);
    }

    private static ImmutableArray<int> RandomDamageTargets(RuleGameState state, PlayerSide sourceSide)
    {
        var opponent = state.Player(RuleGameState.Other(sourceSide));
        return new[] { opponent.Hero.EntityId }
            .Concat(opponent.Board
                .Where(minion => !minion.Dormant)
                .OrderBy(minion => minion.BoardPosition)
                .Select(minion => minion.EntityId))
            .ToImmutableArray();
    }

    private static PlayerSide FindSourceSide(RuleGameState state, int? sourceEntityId)
    {
        if (sourceEntityId is not int entityId)
            return state.ActiveSide;
        if (ContainsEntity(state.Friendly, entityId))
            return PlayerSide.Friendly;
        if (ContainsEntity(state.Opponent, entityId))
            return PlayerSide.Opponent;
        return state.ActiveSide;
    }

    private static bool ContainsEntity(PlayerState player, int entityId) =>
        player.Hero.EntityId == entityId ||
        player.HeroPower.EntityId == entityId ||
        player.Weapon?.EntityId == entityId ||
        player.Hand.Any(card => card.EntityId == entityId) ||
        player.Deck.Any(card => card.EntityId == entityId) ||
        player.Board.Any(minion => minion.EntityId == entityId) ||
        player.Locations.Any(location => location.EntityId == entityId) ||
        player.Graveyard.Any(card => card.EntityId == entityId);

    private static int FindPendingIndex(ImmutableArray<RuleEvent> events)
    {
        for (var index = 0; index < events.Length; index++)
        {
            if (events[index].Type is RandomDamagePending or RandomSummonPending or
                DiscardWarlockRuleEngine.ContinueWickedWhispersPending or
                DiscardWarlockRuleEngine.ContinueChamberDrawPending or
                DiscardWarlockRuleEngine.ContinueEndTurnPending or
                DiscardWarlockRuleEngine.ContinueLifeTapDamagePending or
                DiscardWarlockRuleEngine.RandomDrawPending or
                DiscardWarlockRuleEngine.RandomTemporaryDrawPending or
                DiscardWarlockRuleEngine.RandomBoundDrawPending)
                return index;
        }

        return -1;
    }

    private static bool IsTerminal(RuleGameState state) =>
        state.Friendly.Hero.Health <= 0 || state.Opponent.Hero.Health <= 0;

    private static ImmutableArray<RuleEvent> ReplacePending(
        ImmutableArray<RuleEvent> events,
        int pendingIndex,
        ImmutableArray<RuleEvent> replacement) =>
        events.Take(pendingIndex)
            .Concat(replacement)
            .Concat(events.Skip(pendingIndex + 1))
            .ToImmutableArray();

    private static ImmutableArray<RandomOutcome> Consolidate(IEnumerable<RandomOutcome> outcomes) => outcomes
        .GroupBy(OutcomeKey, StringComparer.Ordinal)
        .Select(group =>
        {
            var representative = group.First();
            return representative with
            {
                Probability = group.Sum(outcome => outcome.Probability),
                UsesMonteCarlo = group.Any(outcome => outcome.UsesMonteCarlo)
            };
        })
        .ToImmutableArray();

    private static ImmutableArray<PartialOutcome> ConsolidatePartials(IEnumerable<PartialOutcome> outcomes) => outcomes
        .GroupBy(outcome => RuleStateKey.Calculate(outcome.State), StringComparer.Ordinal)
        .Select(group =>
        {
            var representative = group.First();
            return representative with { Probability = group.Sum(outcome => outcome.Probability) };
        })
        .ToImmutableArray();

    private static string OutcomeKey(RandomOutcome outcome)
    {
        var pending = string.Join(",", outcome.Events
            .Where(ruleEvent => ruleEvent.Type.EndsWith("_pending", StringComparison.Ordinal))
            .Select(ruleEvent =>
                $"{ruleEvent.Type}:{ruleEvent.SourceEntityId}:{ruleEvent.TargetEntityId}:{ruleEvent.Amount}:{ruleEvent.CardId}"));
        return RuleStateKey.Calculate(outcome.State) + "|" + pending;
    }

    private sealed record PartialOutcome(
        RuleGameState State,
        ImmutableArray<RuleEvent> Events,
        double Probability);

    private sealed record DamageResult(RuleGameState State, ImmutableArray<RuleEvent> Events);

    private sealed record SummonResult(RuleGameState State, ImmutableArray<RuleEvent> Events);
}
