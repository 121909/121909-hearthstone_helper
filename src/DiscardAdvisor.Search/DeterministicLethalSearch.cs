using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using DiscardAdvisor.Rules;
using DiscardAdvisor.Rules.Model;

namespace DiscardAdvisor.Search;

public sealed record LethalSearchResult(
    bool Found,
    SearchRoute? Route,
    int ExploredNodes,
    TimeSpan Elapsed,
    bool TimedOut,
    bool Cancelled);

public sealed class DeterministicLethalSearch
{
    private const int MaximumFrontierSize = 256;
    private const int FrontierPruneThreshold = MaximumFrontierSize * 4;
    private readonly LegalActionEnumerator _actions;
    private readonly DiscardWarlockRuleEngine _rules;
    private readonly IStateEvaluator _evaluator;

    public DeterministicLethalSearch()
        : this(new LegalActionEnumerator(), new DiscardWarlockRuleEngine(), new FastStateEvaluator())
    {
    }

    public DeterministicLethalSearch(
        LegalActionEnumerator actions,
        DiscardWarlockRuleEngine rules,
        IStateEvaluator evaluator)
    {
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    }

    public LethalSearchResult Search(
        RuleGameState initialState,
        int maximumActions,
        TimeSpan timeBudget,
        CancellationToken cancellationToken = default)
    {
        if (initialState is null)
            throw new ArgumentNullException(nameof(initialState));
        if (maximumActions < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumActions));
        if (timeBudget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeBudget));

        var stopwatch = Stopwatch.StartNew();
        var expansionBudget = CalculateExpansionBudget(timeBudget);
        var frontier = ImmutableArray.Create(new SearchRoute(
            initialState,
            ImmutableArray<RuleAction>.Empty,
            ImmutableArray<RuleEvent>.Empty,
            1,
            _evaluator.Evaluate(initialState)));
        var seen = new HashSet<string>(StringComparer.Ordinal) { RuleStateKey.Calculate(initialState) };
        var explored = 0;

        for (var depth = 0; depth < maximumActions && !frontier.IsEmpty; depth++)
        {
            var next = new List<SearchRoute>();
            foreach (var route in frontier)
            {
                if (cancellationToken.IsCancellationRequested)
                    return Result(false, null, explored, stopwatch, false, true);
                if (stopwatch.Elapsed >= expansionBudget)
                    return Result(false, null, explored, stopwatch, true, false);

                explored++;
                foreach (var action in _actions.Enumerate(route.State).Where(action => action is not EndTurnAction))
                {
                    if (cancellationToken.IsCancellationRequested)
                        return Result(false, null, explored, stopwatch, false, true);
                    if (stopwatch.Elapsed >= expansionBudget)
                        return Result(false, null, explored, stopwatch, true, false);

                    var transition = _rules.Apply(route.State, action);
                    if (!transition.IsLegal)
                        continue;
                    var allBranchesLethal = !transition.Branches.IsEmpty &&
                                            transition.Branches.All(branch => IsLethal(branch.State));
                    if (IsLethal(transition.State) || allBranchesLethal)
                    {
                        var lethalRoute = new SearchRoute(
                            transition.State,
                            route.Actions.Add(action),
                            route.Events.AddRange(transition.Events),
                            route.Probability,
                            _evaluator.Evaluate(transition.State));
                        return Result(true, lethalRoute, explored, stopwatch, false, false);
                    }
                    if (!transition.Branches.IsEmpty || HasUnresolvedRandomness(transition.Events) ||
                        transition.State.Friendly.Hero.Health <= 0)
                        continue;

                    var key = RuleStateKey.Calculate(transition.State);
                    if (!seen.Add(key))
                        continue;
                    next.Add(new SearchRoute(
                        transition.State,
                        route.Actions.Add(action),
                        route.Events.AddRange(transition.Events),
                        route.Probability,
                        _evaluator.Evaluate(transition.State)));
                    if (next.Count >= FrontierPruneThreshold)
                        next = Prioritize(next).Take(MaximumFrontierSize).ToList();
                }
            }
            frontier = Prioritize(next)
                .Take(MaximumFrontierSize)
                .ToImmutableArray();
        }
        return Result(false, null, explored, stopwatch, false, false);
    }

    private static bool IsLethal(RuleGameState state) =>
        state.Opponent.Hero.Health <= 0 && state.Friendly.Hero.Health > 0;

    private static IOrderedEnumerable<SearchRoute> Prioritize(IEnumerable<SearchRoute> routes) => routes
        .OrderBy(route => route.State.Opponent.Hero.Health)
        .ThenByDescending(route => route.Score);

    private static TimeSpan CalculateExpansionBudget(TimeSpan totalBudget)
    {
        var reserveTicks = Math.Min(TimeSpan.FromMilliseconds(30).Ticks, totalBudget.Ticks / 2);
        return totalBudget - TimeSpan.FromTicks(reserveTicks);
    }

    private static bool HasUnresolvedRandomness(IEnumerable<RuleEvent> events) => events.Any(ruleEvent =>
        ruleEvent.Type.StartsWith("random_", StringComparison.Ordinal) &&
        ruleEvent.Type.EndsWith("_pending", StringComparison.Ordinal));

    private static LethalSearchResult Result(
        bool found,
        SearchRoute? route,
        int explored,
        Stopwatch stopwatch,
        bool timedOut,
        bool cancelled)
    {
        stopwatch.Stop();
        return new LethalSearchResult(found, route, explored, stopwatch.Elapsed, timedOut, cancelled);
    }
}
