using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using DiscardAdvisor.Rules;
using DiscardAdvisor.Rules.Model;

namespace DiscardAdvisor.Search;

public sealed class BeamSearch
{
    private readonly LegalActionEnumerator _actions;
    private readonly DiscardWarlockRuleEngine _rules;
    private readonly IStateEvaluator _evaluator;
    private readonly DominancePruner _dominancePruner;
    private readonly RandomOutcomeSampler _randomOutcomes;
    private readonly RiskAwareRouteRanker _routeRanker;

    public BeamSearch()
        : this(
            new LegalActionEnumerator(),
            new DiscardWarlockRuleEngine(),
            new MultiDimensionalStateEvaluator(),
            new DominancePruner(),
            new RandomOutcomeSampler())
    {
    }

    public BeamSearch(IRandomOneCostMinionPool oneCostMinions)
        : this(
            new LegalActionEnumerator(),
            new DiscardWarlockRuleEngine(),
            new MultiDimensionalStateEvaluator(),
            new DominancePruner(),
            new RandomOutcomeSampler(oneCostMinions))
    {
    }

    public BeamSearch(
        LegalActionEnumerator actions,
        DiscardWarlockRuleEngine rules,
        IStateEvaluator evaluator,
        DominancePruner dominancePruner)
        : this(actions, rules, evaluator, dominancePruner, new RandomOutcomeSampler())
    {
    }

    public BeamSearch(
        LegalActionEnumerator actions,
        DiscardWarlockRuleEngine rules,
        IStateEvaluator evaluator,
        DominancePruner dominancePruner,
        RandomOutcomeSampler randomOutcomes)
    {
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _dominancePruner = dominancePruner ?? throw new ArgumentNullException(nameof(dominancePruner));
        _randomOutcomes = randomOutcomes ?? throw new ArgumentNullException(nameof(randomOutcomes));
        _routeRanker = new RiskAwareRouteRanker(evaluator as IDetailedStateEvaluator);
    }

    public BeamSearchResult Search(
        RuleGameState initialState,
        BeamSearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (initialState is null)
            throw new ArgumentNullException(nameof(initialState));
        options ??= new BeamSearchOptions();
        ValidateOptions(options);
        var opponentBelief = options.OpponentBelief ?? new OpponentBeliefModel().Estimate(initialState);
        var expansionTimeBudget = CalculateExpansionTimeBudget(options.EffectiveTimeBudget);

        var stopwatch = Stopwatch.StartNew();
        var frontier = ImmutableArray.Create(new SearchRoute(
            initialState,
            ImmutableArray<RuleAction>.Empty,
            ImmutableArray<RuleEvent>.Empty,
            1d,
            Evaluate(initialState, opponentBelief)));
        var completed = new List<SearchRoute>();
        var seenAtDepth = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [RuleStateKey.Calculate(initialState)] = 0
        };
        var expanded = 0;
        var generated = 0;
        var deduplicated = 0;
        var dominancePruned = 0;
        var timedOut = false;
        var cancelled = false;
        var random = new SeededRandomSource(options.EffectiveRandomSampling.Seed);

        for (var depth = 0; depth < options.MaximumActions && !frontier.IsEmpty; depth++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }
            if (stopwatch.Elapsed >= expansionTimeBudget)
            {
                timedOut = true;
                break;
            }

            var next = new List<SearchRoute>();
            foreach (var route in frontier)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                if (stopwatch.Elapsed >= expansionTimeBudget)
                {
                    timedOut = true;
                    break;
                }

                expanded++;
                foreach (var action in _actions.Enumerate(route.State))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }
                    if (stopwatch.Elapsed >= expansionTimeBudget)
                    {
                        timedOut = true;
                        break;
                    }

                    var transition = _rules.Apply(route.State, action);
                    if (!transition.IsLegal)
                        continue;
                    var outcomes = _randomOutcomes.Resolve(
                        transition,
                        options.EffectiveRandomSampling,
                        random,
                        () => cancellationToken.IsCancellationRequested || stopwatch.Elapsed >= expansionTimeBudget);
                    foreach (var outcome in outcomes)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            cancelled = true;
                            break;
                        }
                        if (stopwatch.Elapsed >= expansionTimeBudget)
                        {
                            timedOut = true;
                            break;
                        }

                        generated++;
                        var candidate = new SearchRoute(
                            outcome.State,
                            route.Actions.Add(action),
                            route.Events.AddRange(outcome.Events),
                            route.Probability * outcome.Probability,
                            Evaluate(outcome.State, opponentBelief),
                            route.UsesMonteCarlo || outcome.UsesMonteCarlo);
                        var terminal = action is EndTurnAction ||
                                       RequiresClientChoice(outcome.Events) ||
                                       outcome.State.Opponent.Hero.Health <= 0 ||
                                       outcome.State.Friendly.Hero.Health <= 0;
                        if (terminal)
                        {
                            completed.Add(candidate);
                            continue;
                        }

                        next.Add(candidate);
                    }
                    if (cancelled || timedOut)
                        break;
                }
                if (cancelled || timedOut)
                    break;
            }
            if (cancelled)
                break;
            if (timedOut)
            {
                frontier = next.OrderByDescending(RoutePriority)
                    .Take(options.BeamWidth)
                    .ToImmutableArray();
                break;
            }

            var uniqueNext = new List<SearchRoute>();
            var preselected = next.OrderByDescending(RoutePriority)
                .Take(options.BeamWidth * 2)
                .ToArray();
            var bestByState = new Dictionary<string, SearchRoute>(StringComparer.Ordinal);
            foreach (var route in preselected)
            {
                if (stopwatch.Elapsed >= expansionTimeBudget)
                {
                    timedOut = true;
                    break;
                }

                var key = RuleStateKey.Calculate(route.State);
                if (bestByState.TryGetValue(key, out var existing))
                {
                    deduplicated++;
                    if (RoutePriority(route) > RoutePriority(existing))
                        bestByState[key] = route;
                }
                else
                {
                    bestByState.Add(key, route);
                }
            }
            foreach (var entry in bestByState)
            {
                if (seenAtDepth.TryGetValue(entry.Key, out var seenDepth) && seenDepth < depth + 1)
                {
                    deduplicated++;
                    continue;
                }
                seenAtDepth[entry.Key] = depth + 1;
                uniqueNext.Add(entry.Value);
            }
            if (timedOut)
            {
                frontier = uniqueNext.OrderByDescending(RoutePriority)
                    .Take(options.BeamWidth)
                    .ToImmutableArray();
                break;
            }
            var pruned = _dominancePruner.Prune(uniqueNext, out var prunedThisDepth);
            dominancePruned += prunedThisDepth;
            frontier = pruned
                .OrderByDescending(RoutePriority)
                .Take(options.BeamWidth)
                .ToImmutableArray();
        }

        var finalRoutes = completed.Concat(frontier)
            .GroupBy(route => RuleStateKey.Calculate(route.State))
            .Select(group => group.OrderByDescending(RoutePriority).First())
            .ToImmutableArray();
        var maximumRankingGroups = Math.Max(options.TopK * 2, 8);
        var rankingRoutes = finalRoutes
            .GroupBy(RiskAwareRouteRanker.ActionSequenceKey, StringComparer.Ordinal)
            .OrderByDescending(group => group.Max(RoutePriority))
            .Take(maximumRankingGroups)
            .SelectMany(group => group)
            .ToImmutableArray();
        var candidates = _routeRanker.Rank(
            initialState,
            rankingRoutes,
            opponentBelief,
            options.TopK);
        var candidateRanks = candidates
            .Select((candidate, index) => (Key: RiskAwareRouteRanker.ActionSequenceKey(candidate.RepresentativeRoute), Rank: index))
            .ToDictionary(entry => entry.Key, entry => entry.Rank, StringComparer.Ordinal);
        var routes = finalRoutes
            .Where(route => candidateRanks.ContainsKey(RiskAwareRouteRanker.ActionSequenceKey(route)))
            .OrderBy(route => candidateRanks[RiskAwareRouteRanker.ActionSequenceKey(route)])
            .ThenByDescending(RoutePriority)
            .Take(options.TopK)
            .ToImmutableArray();
        stopwatch.Stop();
        return new BeamSearchResult(
            routes,
            new BeamSearchMetrics(
                expanded,
                generated,
                deduplicated,
                dominancePruned,
                stopwatch.Elapsed,
                timedOut,
                cancelled),
            candidates);
    }

    private static double RoutePriority(SearchRoute route) => route.Score + Math.Log(Math.Max(route.Probability, 1e-12));

    private static bool RequiresClientChoice(IEnumerable<RuleEvent> events) => events.Any(ruleEvent =>
        ruleEvent.Type is "choice_pending" or "hand_discard_choice_pending");

    private double Evaluate(RuleGameState state, OpponentBelief belief) => _evaluator is IDetailedStateEvaluator detailed
        ? detailed.EvaluateDetailed(state, belief).Total
        : _evaluator.Evaluate(state);

    private static TimeSpan CalculateExpansionTimeBudget(TimeSpan totalBudget)
    {
        var reserveTicks = Math.Min(TimeSpan.FromMilliseconds(50).Ticks, totalBudget.Ticks / 4);
        return totalBudget - TimeSpan.FromTicks(reserveTicks);
    }

    private static void ValidateOptions(BeamSearchOptions options)
    {
        if (options.BeamWidth < 1)
            throw new ArgumentOutOfRangeException(nameof(options.BeamWidth));
        if (options.MaximumActions < 1)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumActions));
        if (options.TopK < 1)
            throw new ArgumentOutOfRangeException(nameof(options.TopK));
        if (options.EffectiveTimeBudget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.TimeBudget));
        options.EffectiveRandomSampling.Validate();
    }
}
