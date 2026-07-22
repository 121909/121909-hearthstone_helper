using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using DiscardAdvisor.Rules.Model;

namespace DiscardAdvisor.Search;

public sealed record RiskSample(double Value, double Probability, bool IsLethal = false);

public sealed record RouteRiskStatistics(
    double Expected,
    double P10,
    double Variance,
    double LethalProbability,
    double CoverageProbability)
{
    public static RouteRiskStatistics Calculate(
        IEnumerable<RiskSample> samples,
        double uncoveredOutcomePenalty = 25d)
    {
        if (samples is null)
            throw new ArgumentNullException(nameof(samples));
        if (uncoveredOutcomePenalty < 0 || double.IsNaN(uncoveredOutcomePenalty) || double.IsInfinity(uncoveredOutcomePenalty))
            throw new ArgumentOutOfRangeException(nameof(uncoveredOutcomePenalty));

        var supplied = samples.ToList();
        if (supplied.Any(sample =>
                double.IsNaN(sample.Value) || double.IsInfinity(sample.Value) ||
                double.IsNaN(sample.Probability) || double.IsInfinity(sample.Probability) ||
                sample.Probability < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(samples));
        }

        var weighted = supplied.Where(sample => sample.Probability > 0).ToList();
        if (weighted.Count == 0)
            throw new ArgumentException("At least one positive-probability sample is required.", nameof(samples));

        var probabilityTotal = weighted.Sum(sample => sample.Probability);
        var coverage = Math.Min(1d, probabilityTotal);
        if (probabilityTotal > 1d)
        {
            var scale = 1d / probabilityTotal;
            weighted = weighted.Select(sample => sample with { Probability = sample.Probability * scale }).ToList();
        }
        else if (probabilityTotal < 1d)
        {
            var worst = Math.Min(0d, weighted.Min(sample => sample.Value) - uncoveredOutcomePenalty);
            weighted.Add(new RiskSample(worst, 1d - probabilityTotal));
        }

        var expected = weighted.Sum(sample => sample.Value * sample.Probability);
        var variance = weighted.Sum(sample =>
            sample.Probability * (sample.Value - expected) * (sample.Value - expected));
        var lethalProbability = weighted.Where(sample => sample.IsLethal).Sum(sample => sample.Probability);
        var cumulative = 0d;
        var p10 = weighted.OrderBy(sample => sample.Value)
            .First(sample => (cumulative += sample.Probability) >= 0.1d)
            .Value;
        return new RouteRiskStatistics(expected, p10, variance, lethalProbability, coverage);
    }
}

public sealed record ScoredRouteOutcome(
    SearchRoute Route,
    DetailedStateScore Score);

public sealed record RiskAwareRouteCandidate(
    string CandidateId,
    ImmutableArray<RuleAction> Actions,
    ImmutableArray<ScoredRouteOutcome> Outcomes,
    ScoreDimensions ExpectedDimensions,
    RouteRiskStatistics Risk,
    double RiskAdjustedScore,
    double Confidence,
    SearchRoute RepresentativeRoute);

public sealed class RiskAwareRouteRanker
{
    private readonly IDetailedStateEvaluator _evaluator;

    public RiskAwareRouteRanker(IDetailedStateEvaluator? evaluator = null)
    {
        _evaluator = evaluator ?? new MultiDimensionalStateEvaluator();
    }

    public ImmutableArray<RiskAwareRouteCandidate> Rank(
        RuleGameState initialState,
        IEnumerable<SearchRoute> routes,
        OpponentBelief? belief = null,
        int maximumCandidates = 5)
    {
        if (initialState is null)
            throw new ArgumentNullException(nameof(initialState));
        if (routes is null)
            throw new ArgumentNullException(nameof(routes));
        if (maximumCandidates < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumCandidates));
        belief ??= OpponentBelief.Balanced;

        return routes.GroupBy(ActionSequenceKey, StringComparer.Ordinal)
            .Select(group => CreateCandidate(initialState, group, belief))
            .OrderByDescending(candidate => candidate.RiskAdjustedScore)
            .ThenByDescending(candidate => candidate.Risk.LethalProbability)
            .ThenByDescending(candidate => candidate.Risk.Expected)
            .Take(maximumCandidates)
            .Select((candidate, index) => candidate with
            {
                CandidateId = "route-" + (index + 1).ToString(CultureInfo.InvariantCulture)
            })
            .ToImmutableArray();
    }

    public static string ActionSequenceKey(SearchRoute route)
    {
        if (route is null)
            throw new ArgumentNullException(nameof(route));
        return string.Join(">", route.Actions.Select(ActionKey));
    }

    private RiskAwareRouteCandidate CreateCandidate(
        RuleGameState initialState,
        IEnumerable<SearchRoute> routeGroup,
        OpponentBelief belief)
    {
        var outcomes = routeGroup.Select(route => new ScoredRouteOutcome(
                route,
                _evaluator.EvaluateRoute(initialState, route, belief)))
            .ToImmutableArray();
        var risk = RouteRiskStatistics.Calculate(outcomes.Select(outcome => new RiskSample(
            outcome.Score.Total,
            outcome.Route.Probability,
            outcome.Route.State.Opponent.Hero.Health <= 0 && outcome.Route.State.Friendly.Hero.Health > 0)));
        var coveredProbability = outcomes.Sum(outcome => Math.Max(0, outcome.Route.Probability));
        var expectedDimensions = coveredProbability <= 0
            ? ScoreDimensions.Zero
            : outcomes.Aggregate(
                    ScoreDimensions.Zero,
                    (current, outcome) => current.Add(
                        outcome.Score.Dimensions.Scale(Math.Max(0, outcome.Route.Probability))))
                .Scale(1d / coveredProbability);
        var riskAversion = 0.2d + belief.Aggro * 0.45d + belief.Control * 0.25d + belief.Combo * 0.15d;
        var downside = Math.Max(0, risk.Expected - risk.P10);
        var riskAdjusted = risk.Expected - riskAversion * downside - Math.Sqrt(risk.Variance) * 0.08d;
        var representative = outcomes
            .OrderByDescending(outcome => outcome.Score.Total)
            .ThenByDescending(outcome => outcome.Route.Probability)
            .First().Route;
        var confidence = CalculateConfidence(outcomes, risk.CoverageProbability);
        return new RiskAwareRouteCandidate(
            string.Empty,
            representative.Actions,
            outcomes,
            expectedDimensions,
            risk,
            riskAdjusted,
            confidence,
            representative);
    }

    private static double CalculateConfidence(
        ImmutableArray<ScoredRouteOutcome> outcomes,
        double coverageProbability)
    {
        var confidence = coverageProbability;
        if (outcomes.Any(outcome => outcome.Route.UsesMonteCarlo))
            confidence *= 0.9d;
        if (outcomes.SelectMany(outcome => outcome.Route.Events).Any(ruleEvent =>
                ruleEvent.Type == "random_summon_effect_unmodeled"))
        {
            confidence *= 0.85d;
        }
        if (outcomes.SelectMany(outcome => outcome.Route.Events).Any(ruleEvent =>
                ruleEvent.Type.EndsWith("_unresolved", StringComparison.Ordinal)))
        {
            confidence *= 0.65d;
        }
        return Math.Max(0, Math.Min(1, confidence));
    }

    private static string ActionKey(RuleAction action) => action switch
    {
        PlayCardAction play => string.Join(
            ":",
            "play",
            (int)play.Side,
            play.SourceEntityId,
            play.TargetEntityId,
            play.BoardPosition),
        AttackAction attack => $"attack:{(int)attack.Side}:{attack.SourceEntityId}:{attack.TargetEntityId}",
        UseHeroPowerAction heroPower => $"power:{(int)heroPower.Side}:{heroPower.TargetEntityId}",
        UseLocationAction location =>
            $"location:{(int)location.Side}:{location.SourceEntityId}:{location.SelectedEntityId}",
        SelectChoiceAction choice => $"choice:{(int)choice.Side}:{choice.ChoiceId}:{choice.SelectedEntityId}",
        EndTurnAction endTurn => $"end:{(int)endTurn.Side}",
        _ => action.ToString()
    };
}
