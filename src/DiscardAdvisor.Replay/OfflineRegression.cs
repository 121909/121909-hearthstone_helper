using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using DiscardAdvisor.Rules;
using DiscardAdvisor.Rules.Model;
using DiscardAdvisor.Search;

namespace DiscardAdvisor.Replay;

public sealed record OfflineRegressionOptions(
    int BeamWidth = 64,
    int MaximumActions = 12,
    int TopK = 5,
    int TimeBudgetMs = 250,
    int Seed = 0x5EED)
{
    public void Validate()
    {
        if (BeamWidth < 1)
            throw new ArgumentOutOfRangeException(nameof(BeamWidth));
        if (MaximumActions < 1)
            throw new ArgumentOutOfRangeException(nameof(MaximumActions));
        if (TopK < 1)
            throw new ArgumentOutOfRangeException(nameof(TopK));
        if (TimeBudgetMs < 1)
            throw new ArgumentOutOfRangeException(nameof(TimeBudgetMs));
    }
}

public sealed record SnapshotEvaluation(
    string Source,
    string StateId,
    bool MappingSupported,
    int CandidateCount,
    int RouteCount,
    int LegalRouteCount,
    double ElapsedMs,
    bool DeadlineExpired,
    bool? ExpertTop3Matched,
    ImmutableArray<string> UnsupportedInteractions);

public sealed record OfflineRegressionReport(
    DateTimeOffset GeneratedAtUtc,
    OfflineRegressionOptions Options,
    int ReplayCount,
    int SnapshotCount,
    int EvaluatedSnapshotCount,
    int AnnotatedSnapshotCount,
    int ExpertTop3MatchCount,
    int RouteCount,
    int LegalRouteCount,
    int DeadlineExpiredCount,
    double LatencyP50Ms,
    double LatencyP95Ms,
    double LatencyMaximumMs,
    ImmutableDictionary<string, int> UnsupportedInteractions,
    ImmutableArray<string> InputErrors,
    ImmutableArray<ReplayArchiveSummary> Replays,
    ImmutableArray<SnapshotEvaluation> Snapshots)
{
    public double LegalRouteRate => RouteCount == 0 ? 0 : (double)LegalRouteCount / RouteCount;

    public double? ExpertTop3ConsistencyRate => AnnotatedSnapshotCount == 0
        ? null
        : (double)ExpertTop3MatchCount / AnnotatedSnapshotCount;

    public double DeadlineExpirationRate => EvaluatedSnapshotCount == 0
        ? 0
        : (double)DeadlineExpiredCount / EvaluatedSnapshotCount;

    public bool Passed => SnapshotCount > 0 &&
                          EvaluatedSnapshotCount == SnapshotCount &&
                          RouteCount > 0 &&
                          LegalRouteCount == RouteCount &&
                          InputErrors.IsEmpty;
}

public sealed class OfflineRegressionRunner
{
    private readonly SnapshotRuleStateMapper _mapper;
    private readonly LocalTurnAdvisor _advisor;
    private readonly RouteLegalityVerifier _legalityVerifier;
    private readonly Func<DateTimeOffset> _utcNow;

    public OfflineRegressionRunner(
        SnapshotRuleStateMapper? mapper = null,
        LocalTurnAdvisor? advisor = null,
        RouteLegalityVerifier? legalityVerifier = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _mapper = mapper ?? new SnapshotRuleStateMapper();
        _advisor = advisor ?? new LocalTurnAdvisor();
        _legalityVerifier = legalityVerifier ?? new RouteLegalityVerifier();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public OfflineRegressionReport Run(
        RegressionInputSet input,
        OfflineRegressionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (input is null)
            throw new ArgumentNullException(nameof(input));
        options ??= new OfflineRegressionOptions();
        options.Validate();

        var evaluations = ImmutableArray.CreateBuilder<SnapshotEvaluation>();
        var unsupported = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var fixture in input.Snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshotUnsupported = fixture.Snapshot.Derived.UnsupportedInteractions.ToList();
            var mapping = _mapper.Map(fixture.Snapshot);
            snapshotUnsupported.AddRange(mapping.UnsupportedInteractions);
            AddUnsupported(unsupported, snapshotUnsupported);
            if (!mapping.IsSupported || mapping.State is null)
            {
                evaluations.Add(new SnapshotEvaluation(
                    fixture.Source,
                    fixture.Snapshot.StateId,
                    false,
                    0,
                    0,
                    0,
                    0,
                    false,
                    null,
                    snapshotUnsupported.Distinct(StringComparer.Ordinal).ToImmutableArray()));
                continue;
            }

            var searchOptions = new LocalAdvisorOptions(new BeamSearchOptions(
                options.BeamWidth,
                options.MaximumActions,
                options.TopK,
                TimeSpan.FromMilliseconds(options.TimeBudgetMs),
                new RandomSamplingOptions(options.Seed)));
            var stopwatch = Stopwatch.StartNew();
            var result = _advisor.Advise(mapping.State, searchOptions, cancellationToken);
            stopwatch.Stop();
            var routes = result.Candidates.Select(candidate => candidate.Actions).ToArray();
            var legalRouteCount = routes.Count(actions =>
                _legalityVerifier.IsLegal(mapping.State, actions, options.Seed));
            var top3Matched = input.Annotations.TryGetValue(fixture.Snapshot.StateId, out var annotation)
                ? MatchesExpertTop3(result.Candidates, annotation)
                : (bool?)null;
            var elapsedMs = Math.Max(result.Elapsed.TotalMilliseconds, stopwatch.Elapsed.TotalMilliseconds);
            evaluations.Add(new SnapshotEvaluation(
                fixture.Source,
                fixture.Snapshot.StateId,
                true,
                result.Candidates.Length,
                routes.Length,
                legalRouteCount,
                elapsedMs,
                fixture.Snapshot.RemainingTurnTimeMs > 0 && elapsedMs >= fixture.Snapshot.RemainingTurnTimeMs,
                top3Matched,
                snapshotUnsupported.Distinct(StringComparer.Ordinal).ToImmutableArray()));
        }

        var values = evaluations.ToImmutable();
        var latencies = values.Where(value => value.MappingSupported).Select(value => value.ElapsedMs).OrderBy(value => value).ToArray();
        return new OfflineRegressionReport(
            _utcNow(),
            options,
            input.Replays.Length,
            input.Snapshots.Length,
            values.Count(value => value.MappingSupported),
            values.Count(value => value.ExpertTop3Matched.HasValue),
            values.Count(value => value.ExpertTop3Matched == true),
            values.Sum(value => value.RouteCount),
            values.Sum(value => value.LegalRouteCount),
            values.Count(value => value.DeadlineExpired),
            Percentile(latencies, 0.50),
            Percentile(latencies, 0.95),
            latencies.Length == 0 ? 0 : latencies[^1],
            unsupported.ToImmutableDictionary(StringComparer.Ordinal),
            input.Errors,
            input.Replays,
            values);
    }

    private static bool MatchesExpertTop3(
        IEnumerable<RiskAwareRouteCandidate> candidates,
        ExpertAnnotation annotation)
    {
        var advisorTop3 = candidates.Take(3).Select(candidate => candidate.Actions).ToArray();
        return annotation.ExpertTop3.Any(expert => advisorTop3.Any(candidate =>
            candidate.Length == expert.Actions.Length &&
            candidate.Zip(expert.Actions, (action, expected) => expected.Matches(action)).All(value => value)));
    }

    private static void AddUnsupported(IDictionary<string, int> destination, IEnumerable<string> values)
    {
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
            destination[value] = destination.TryGetValue(value, out var count) ? count + 1 : 1;
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
            return 0;
        var rank = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Max(0, Math.Min(rank, sorted.Count - 1))];
    }
}

public sealed class RouteLegalityVerifier
{
    private readonly DiscardWarlockRuleEngine _rules = new();
    private readonly RandomOutcomeSampler _randomOutcomes = new();

    public bool IsLegal(RuleGameState initialState, IEnumerable<RuleAction> actions, int seed)
    {
        if (initialState is null)
            throw new ArgumentNullException(nameof(initialState));
        if (actions is null)
            throw new ArgumentNullException(nameof(actions));

        var states = new List<RuleGameState> { initialState };
        var random = new Random(seed);
        foreach (var action in actions)
        {
            var next = new List<RuleGameState>();
            foreach (var state in states)
            {
                var transition = _rules.Apply(state, action);
                if (!transition.IsLegal)
                    continue;
                var outcomes = _randomOutcomes.Resolve(
                    transition,
                    new RandomSamplingOptions(seed),
                    random);
                next.AddRange(outcomes.Select(outcome => outcome.State));
            }
            if (next.Count == 0)
                return false;
            states = next;
        }
        return true;
    }
}
