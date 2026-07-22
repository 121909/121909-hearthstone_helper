using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using DiscardAdvisor.Rules.Model;

namespace DiscardAdvisor.Search;

public sealed record BeamSearchOptions(
    int BeamWidth = 64,
    int MaximumActions = 12,
    int TopK = 5,
    TimeSpan? TimeBudget = null,
    RandomSamplingOptions? RandomSampling = null,
    OpponentBelief? OpponentBelief = null)
{
    public TimeSpan EffectiveTimeBudget => TimeBudget ?? TimeSpan.FromMilliseconds(250);

    public RandomSamplingOptions EffectiveRandomSampling => RandomSampling ?? new RandomSamplingOptions();
}

public sealed record SearchRoute(
    RuleGameState State,
    ImmutableArray<RuleAction> Actions,
    ImmutableArray<RuleEvent> Events,
    double Probability,
    double Score,
    bool UsesMonteCarlo = false);

public sealed record BeamSearchMetrics(
    int ExpandedNodes,
    int GeneratedNodes,
    int DeduplicatedNodes,
    int DominancePrunedNodes,
    TimeSpan Elapsed,
    bool TimedOut,
    bool Cancelled);

public sealed record BeamSearchResult(
    ImmutableArray<SearchRoute> Routes,
    BeamSearchMetrics Metrics,
    ImmutableArray<RiskAwareRouteCandidate> Candidates);

public interface IStateEvaluator
{
    double Evaluate(RuleGameState state);
}

public sealed class FastStateEvaluator : IStateEvaluator
{
    public double Evaluate(RuleGameState state)
    {
        var friendly = state.Friendly;
        var opponent = state.Opponent;
        if (opponent.Hero.Health <= 0)
            return 1_000_000;
        if (friendly.Hero.Health <= 0)
            return -1_000_000;
        return (opponent.Hero.MaxHealth - opponent.Hero.Health) * 100d +
               friendly.Board.Sum(minion => minion.Attack * 3d + minion.Health) -
               opponent.Board.Sum(minion => minion.Attack * 3d + minion.Health) +
               friendly.Hand.Length * 2d + friendly.Mana.Available * 0.25d +
               friendly.Hero.Health + friendly.Hero.Armor - opponent.Hero.Health;
    }
}
