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

    public BeamSearch()
        : this(
            new LegalActionEnumerator(),
            new DiscardWarlockRuleEngine(),
            new FastStateEvaluator(),
            new DominancePruner())
    {
    }

    public BeamSearch(
        LegalActionEnumerator actions,
        DiscardWarlockRuleEngine rules,
        IStateEvaluator evaluator,
        DominancePruner dominancePruner)
    {
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _dominancePruner = dominancePruner ?? throw new ArgumentNullException(nameof(dominancePruner));
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

        var stopwatch = Stopwatch.StartNew();
        var frontier = ImmutableArray.Create(new SearchRoute(
            initialState,
            ImmutableArray<RuleAction>.Empty,
            ImmutableArray<RuleEvent>.Empty,
            1d,
            _evaluator.Evaluate(initialState)));
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

        for (var depth = 0; depth < options.MaximumActions && !frontier.IsEmpty; depth++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }
            if (stopwatch.Elapsed >= options.EffectiveTimeBudget)
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
                if (stopwatch.Elapsed >= options.EffectiveTimeBudget)
                {
                    timedOut = true;
                    break;
                }

                expanded++;
                foreach (var action in _actions.Enumerate(route.State))
                {
                    var transition = _rules.Apply(route.State, action);
                    if (!transition.IsLegal)
                        continue;
                    var outcomes = transition.Branches.IsEmpty
                        ? new[] { new Outcome(transition.State, transition.Events, 1d) }
                        : transition.Branches.Select(branch => new Outcome(branch.State, branch.Events, branch.Probability));
                    foreach (var outcome in outcomes)
                    {
                        generated++;
                        var candidate = new SearchRoute(
                            outcome.State,
                            route.Actions.Add(action),
                            route.Events.AddRange(outcome.Events),
                            route.Probability * outcome.Probability,
                            _evaluator.Evaluate(outcome.State));
                        var terminal = action is EndTurnAction ||
                                       outcome.State.Opponent.Hero.Health <= 0 ||
                                       outcome.State.Friendly.Hero.Health <= 0;
                        if (terminal)
                        {
                            completed.Add(candidate);
                            continue;
                        }

                        next.Add(candidate);
                    }
                }
            }
            if (cancelled || timedOut)
                break;

            var uniqueNext = new List<SearchRoute>();
            foreach (var group in next.GroupBy(route => RuleStateKey.Calculate(route.State)))
            {
                var best = group.OrderByDescending(RoutePriority).First();
                deduplicated += group.Count() - 1;
                if (seenAtDepth.TryGetValue(group.Key, out var seenDepth) && seenDepth < depth + 1)
                {
                    deduplicated++;
                    continue;
                }
                seenAtDepth[group.Key] = depth + 1;
                uniqueNext.Add(best);
            }
            var pruned = _dominancePruner.Prune(uniqueNext, out var prunedThisDepth);
            dominancePruned += prunedThisDepth;
            frontier = pruned
                .OrderByDescending(RoutePriority)
                .Take(options.BeamWidth)
                .ToImmutableArray();
        }

        stopwatch.Stop();
        var routes = completed.Concat(frontier)
            .GroupBy(route => RuleStateKey.Calculate(route.State))
            .Select(group => group.OrderByDescending(RoutePriority).First())
            .OrderByDescending(RoutePriority)
            .Take(options.TopK)
            .ToImmutableArray();
        return new BeamSearchResult(
            routes,
            new BeamSearchMetrics(
                expanded,
                generated,
                deduplicated,
                dominancePruned,
                stopwatch.Elapsed,
                timedOut,
                cancelled));
    }

    private static double RoutePriority(SearchRoute route) => route.Score + Math.Log(Math.Max(route.Probability, 1e-12));

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
    }

    private sealed record Outcome(RuleGameState State, ImmutableArray<RuleEvent> Events, double Probability);
}
