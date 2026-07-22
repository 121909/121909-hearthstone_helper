using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using DiscardAdvisor.Rules;
using DiscardAdvisor.Rules.Model;
using Xunit;

namespace DiscardAdvisor.Search.Tests;

public sealed class RandomOutcomeSamplerTests
{
    private readonly DiscardWarlockRuleEngine _rules = new();

    [Fact]
    public void ResolvesBarrageMissilesAgainstTheUpdatedTargetPoolExactly()
    {
        var barrage = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.SoulBarrage, 10);
        var target = new MinionState(30, "TARGET", 1, 1, 1, 1);
        var transition = _rules.Apply(
            CreateState(new[] { barrage }, opponentBoard: new[] { target }),
            new PlayCardAction(PlayerSide.Friendly, barrage.EntityId));

        var outcomes = new RandomOutcomeSampler().Resolve(
            transition,
            new RandomSamplingOptions(Seed: 1, ExactOutcomeLimit: 64, MonteCarloSamples: 16),
            new Random(1));

        Assert.Equal(2, outcomes.Length);
        var targetDies = Assert.Single(outcomes.Where(outcome => outcome.State.Opponent.Board.IsEmpty));
        Assert.Equal(26, targetDies.State.Opponent.Hero.Health);
        Assert.Equal(31d / 32d, targetDies.Probability, 10);
        var targetSurvives = Assert.Single(outcomes.Where(outcome => !outcome.State.Opponent.Board.IsEmpty));
        Assert.Equal(25, targetSurvives.State.Opponent.Hero.Health);
        Assert.Equal(1d / 32d, targetSurvives.Probability, 10);
        Assert.All(outcomes, outcome =>
        {
            Assert.False(outcome.UsesMonteCarlo);
            Assert.DoesNotContain(outcome.Events, ruleEvent => ruleEvent.Type.EndsWith("_pending", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void BarrageStopsFiringAfterLethalDamage()
    {
        var barrage = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.SoulBarrage, 10);
        var transition = _rules.Apply(
            CreateState(new[] { barrage }, opponentHealth: 2),
            new PlayCardAction(PlayerSide.Friendly, barrage.EntityId));

        var outcome = Assert.Single(new RandomOutcomeSampler().Resolve(transition, new RandomSamplingOptions()));

        Assert.Equal(0, outcome.State.Opponent.Hero.Health);
        Assert.Equal(2, outcome.Events.Count(ruleEvent => ruleEvent.Type == "damage"));
    }

    [Fact]
    public void WickedWhispersBuffsRandomAcolyteSummonsAfterTheyResolve()
    {
        var whispers = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.WickedWhispers, 10);
        var acolytes = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.DisposableAcolytes, 11);
        var existing = new MinionState(20, "EXISTING", 1, 1, 1, 1);
        var transition = _rules.Apply(
            CreateState(new[] { whispers, acolytes }, board: new[] { existing }),
            new PlayCardAction(PlayerSide.Friendly, whispers.EntityId));
        var pool = new StaticRandomOneCostMinionPool("test", new[]
        {
            new RandomOneCostMinion("ONE", 1, 1)
        });

        var outcome = Assert.Single(new RandomOutcomeSampler(pool).Resolve(transition, new RandomSamplingOptions()));

        Assert.Equal(3, outcome.State.Friendly.Board.Length);
        Assert.All(outcome.State.Friendly.Board, minion => Assert.Equal((2, 2), (minion.Attack, minion.Health)));
        var lastSummon = outcome.Events.Select((ruleEvent, index) => (ruleEvent, index))
            .Where(value => value.ruleEvent.Type == "summon")
            .Max(value => value.index);
        var buff = outcome.Events.Select((ruleEvent, index) => (ruleEvent, index))
            .Single(value => value.ruleEvent.Type == "board_buff").index;
        Assert.True(lastSummon < buff);
        Assert.DoesNotContain(outcome.Events, ruleEvent => ruleEvent.Type.EndsWith("_pending", StringComparison.Ordinal));
    }

    [Fact]
    public void ChamberDoesNotDrawAfterDiscardedBarrageEndsTheGame()
    {
        var barrage = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.SoulBarrage, 11);
        var deck = new[]
        {
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.BonewebEgg, 30),
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.WalkingDead, 31)
        };
        var location = new LocationState(50, DiscardWarlockCardIds.ChamberOfViscidus, 1, 1, 0, 2, true);
        var choice = new PendingChoiceState(
            7,
            "HAND_DISCARD",
            DiscardWarlockCardIds.ChamberOfViscidus,
            ImmutableArray.Create(new ChoiceCandidateState(barrage.EntityId, barrage.CardId)),
            location.EntityId);
        var state = CreateState(
            new[] { barrage },
            locations: new[] { location },
            deck: deck,
            opponentHealth: 2) with { PendingChoice = choice };
        var transition = _rules.Apply(state, new SelectChoiceAction(PlayerSide.Friendly, 7, barrage.EntityId));

        var outcome = Assert.Single(new RandomOutcomeSampler().Resolve(transition, new RandomSamplingOptions()));

        Assert.Equal(0, outcome.State.Opponent.Hero.Health);
        Assert.Equal(2, outcome.State.Friendly.Deck.Length);
        Assert.Empty(outcome.State.Friendly.Hand);
        Assert.DoesNotContain(outcome.Events, ruleEvent => ruleEvent.Type == "draw");
    }

    [Fact]
    public void TemporaryBarrageResolvesBeforeTheNextTemporaryCardExpires()
    {
        var barrage = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.SoulBarrage, 10) with { Temporary = true };
        var handOfGuldan = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.HandOfGuldan, 11) with { Temporary = true };
        var deck = new[]
        {
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.BonewebEgg, 30),
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.WalkingDead, 31),
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.DukeOfBelow, 32)
        };
        var transition = _rules.Apply(
            CreateState(new[] { barrage, handOfGuldan }, deck: deck, opponentHealth: 2),
            new EndTurnAction(PlayerSide.Friendly));

        var outcome = Assert.Single(new RandomOutcomeSampler().Resolve(transition, new RandomSamplingOptions()));

        Assert.Equal(0, outcome.State.Opponent.Hero.Health);
        Assert.Equal(PlayerSide.Friendly, outcome.State.ActiveSide);
        var retained = Assert.Single(outcome.State.Friendly.Hand);
        Assert.Equal(handOfGuldan.EntityId, retained.EntityId);
        Assert.True(retained.Temporary);
        Assert.Equal(1, outcome.State.Friendly.DiscardCount);
        Assert.Equal(3, outcome.State.Friendly.Deck.Length);
    }

    [Fact]
    public void ExactDiscardBranchesResolveNestedBarrageEffects()
    {
        var soulfire = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.Soulfire, 10);
        var barrage = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.SoulBarrage, 11);
        var egg = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.BonewebEgg, 12);
        var transition = _rules.Apply(
            CreateState(new[] { soulfire, barrage, egg }),
            new PlayCardAction(PlayerSide.Friendly, soulfire.EntityId, 200));

        var outcomes = new RandomOutcomeSampler().Resolve(transition, new RandomSamplingOptions());

        Assert.Equal(2, outcomes.Length);
        Assert.All(outcomes, outcome => Assert.Equal(0.5d, outcome.Probability, 10));
        var barrageDiscarded = Assert.Single(outcomes.Where(outcome =>
            outcome.State.Friendly.Graveyard.Any(card => card.CardId == DiscardWarlockCardIds.SoulBarrage)));
        Assert.Equal(21, barrageDiscarded.State.Opponent.Hero.Health);
        Assert.DoesNotContain(barrageDiscarded.Events, ruleEvent =>
            ruleEvent.Type == "random_damage_pending");
    }

    [Fact]
    public void MonteCarloSummonsAreRepeatableForTheSameSeed()
    {
        var acolytes = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.DisposableAcolytes, 10);
        var transition = _rules.Apply(
            CreateState(new[] { acolytes }),
            new PlayCardAction(PlayerSide.Friendly, acolytes.EntityId));
        var pool = new StaticRandomOneCostMinionPool("test", new[]
        {
            new RandomOneCostMinion("ONE_A", 1, 1),
            new RandomOneCostMinion("ONE_B", 2, 1),
            new RandomOneCostMinion("ONE_C", 1, 3, HasUnmodeledEffects: true)
        });
        var sampler = new RandomOutcomeSampler(pool);
        var options = new RandomSamplingOptions(Seed: 731, ExactOutcomeLimit: 1, MonteCarloSamples: 40);

        var first = sampler.Resolve(transition, options, new Random(options.Seed));
        var second = sampler.Resolve(transition, options, new Random(options.Seed));

        Assert.Equal(Signatures(first), Signatures(second));
        Assert.All(first, outcome => Assert.True(outcome.UsesMonteCarlo));
        Assert.Equal(1d, first.Sum(outcome => outcome.Probability), 10);
        Assert.All(first, outcome =>
        {
            Assert.Equal(2, outcome.State.Friendly.Board.Length);
            Assert.Equal(2, outcome.Events.Count(ruleEvent => ruleEvent.Type == "summon"));
        });
        Assert.Contains(first.SelectMany(outcome => outcome.Events), ruleEvent =>
            ruleEvent.Type == "random_summon_effect_unmodeled" && ruleEvent.CardId == "ONE_C");
    }

    [Fact]
    public void LargeSummonPoolFallsBackToMonteCarloBeforeExactExpansion()
    {
        var acolytes = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.DisposableAcolytes, 10);
        var transition = _rules.Apply(
            CreateState(new[] { acolytes }),
            new PlayCardAction(PlayerSide.Friendly, acolytes.EntityId));
        var pool = new StaticRandomOneCostMinionPool(
            "large-test",
            Enumerable.Range(0, 500)
                .Select(index => new RandomOneCostMinion($"ONE_{index:D3}", index % 3, 1 + index % 4)));

        var outcomes = new RandomOutcomeSampler(pool).Resolve(
            transition,
            new RandomSamplingOptions(Seed: 3, ExactOutcomeLimit: 64, MonteCarloSamples: 12));

        Assert.InRange(outcomes.Length, 1, 12);
        Assert.All(outcomes, outcome => Assert.True(outcome.UsesMonteCarlo));
        Assert.Equal(1d, outcomes.Sum(outcome => outcome.Probability), 10);
    }

    [Fact]
    public void RandomSummonsRespectBoardCapacityAndPreserveKeywords()
    {
        var board = Enumerable.Range(0, 6)
            .Select(index => new MinionState(20 + index, $"BOARD_{index}", index + 1, 1, 1, 1))
            .ToArray();
        var state = CreateState(Array.Empty<HandCardState>(), board: board);
        var transition = TransitionResult.Legal(state, new[]
        {
            new RuleEvent(
                "random_one_cost_summon_pending",
                20,
                null,
                2,
                DiscardWarlockCardIds.DisposableAcolytes)
        });
        var pool = new StaticRandomOneCostMinionPool("test", new[]
        {
            new RandomOneCostMinion("KEYWORD_MINION", 2, 3, Taunt: true, Rush: true, Stealth: true)
        });

        var outcome = Assert.Single(new RandomOutcomeSampler(pool).Resolve(
            transition,
            new RandomSamplingOptions(),
            new Random(1)));

        Assert.Equal(7, outcome.State.Friendly.BoardCount);
        var summoned = Assert.Single(outcome.State.Friendly.Board.Where(minion => minion.CardId == "KEYWORD_MINION"));
        Assert.True(summoned.Taunt);
        Assert.True(summoned.Rush);
        Assert.True(summoned.Stealth);
        Assert.Contains(outcome.Events, ruleEvent => ruleEvent.Type == "summon_failed_board_full");
    }

    [Fact]
    public void BeamSearchConsumesResolvedBarrageOutcomes()
    {
        var barrage = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.SoulBarrage, 10);

        var result = new BeamSearch().Search(
            CreateState(new[] { barrage }),
            new BeamSearchOptions(BeamWidth: 64, MaximumActions: 1, TopK: 10, TimeBudget: TimeSpan.FromSeconds(1)));

        var route = Assert.Single(result.Routes.Where(candidate =>
            candidate.Actions.FirstOrDefault() is PlayCardAction action && action.SourceEntityId == barrage.EntityId));
        Assert.Equal(25, route.State.Opponent.Hero.Health);
        Assert.False(route.UsesMonteCarlo);
        Assert.DoesNotContain(route.Events, ruleEvent => ruleEvent.Type == "random_damage_pending");
    }

    [Fact]
    public void MissingMinionPoolProducesExplicitUnresolvedOutcome()
    {
        var acolytes = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.DisposableAcolytes, 10);
        var transition = _rules.Apply(
            CreateState(new[] { acolytes }),
            new PlayCardAction(PlayerSide.Friendly, acolytes.EntityId));

        var outcome = Assert.Single(new RandomOutcomeSampler().Resolve(
            transition,
            new RandomSamplingOptions(),
            new Random(1)));

        Assert.Contains(outcome.Events, ruleEvent => ruleEvent.Type == "random_one_cost_summon_unresolved");
        Assert.DoesNotContain(outcome.Events, ruleEvent => ruleEvent.Type == "random_one_cost_summon_pending");
    }

    private static string[] Signatures(IEnumerable<RandomOutcome> outcomes) => outcomes
        .Select(outcome => string.Join(
            "|",
            RuleStateKey.Calculate(outcome.State),
            outcome.Probability.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            string.Join(",", outcome.Events.Select(ruleEvent => ruleEvent.Type + ":" + ruleEvent.CardId))))
        .ToArray();

    private static RuleGameState CreateState(
        HandCardState[] hand,
        MinionState[]? board = null,
        MinionState[]? opponentBoard = null,
        LocationState[]? locations = null,
        HandCardState[]? deck = null,
        int heroHealth = 30,
        int opponentHealth = 30)
    {
        var friendly = PlayerState.Create(
            new HeroState(100, "HERO", heroHealth, 30),
            new HeroPowerState(101, "POWER", 2),
            new ManaState(10, 0, 0, 10, 0, 0),
            hand,
            board,
            locations,
            deck);
        var opponent = PlayerState.Create(
            new HeroState(200, "OPPONENT_HERO", opponentHealth, 30),
            new HeroPowerState(201, "OPPONENT_POWER", 2),
            new ManaState(0, 0, 0, 0, 0, 0),
            board: opponentBoard);
        return new RuleGameState(1, PlayerSide.Friendly, friendly, opponent);
    }
}
