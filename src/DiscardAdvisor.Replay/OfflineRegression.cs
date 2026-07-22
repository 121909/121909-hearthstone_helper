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
    int TurnNumber,
    bool MappingSupported,
    int CandidateCount,
    int RouteCount,
    int LegalRouteCount,
    double ElapsedMs,
    bool DeadlineExpired,
    bool? ExpertTop3Matched,
    bool AnnotationQualified,
    ImmutableArray<ExpertReviewCandidate> ReviewCandidates,
    ImmutableArray<string> UnsupportedInteractions)
{
    public SnapshotEvaluation(
        string source,
        string stateId,
        int turnNumber,
        bool mappingSupported,
        int candidateCount,
        int routeCount,
        int legalRouteCount,
        double elapsedMs,
        bool deadlineExpired,
        bool? expertTop3Matched,
        ImmutableArray<ExpertReviewCandidate> reviewCandidates,
        ImmutableArray<string> unsupportedInteractions)
        : this(
            source,
            stateId,
            turnNumber,
            mappingSupported,
            candidateCount,
            routeCount,
            legalRouteCount,
            elapsedMs,
            deadlineExpired,
            expertTop3Matched,
            false,
            reviewCandidates,
            unsupportedInteractions)
    {
    }
}

public sealed record ExpertReviewAction(
    AnnotatedAction Annotation,
    string? SourceCardId,
    string? TargetCardId);

public sealed record ExpertReviewCandidate(
    string CandidateId,
    double RiskAdjustedScore,
    double Confidence,
    double ExpectedScore,
    double P10Score,
    double LethalProbability,
    ImmutableArray<ExpertReviewAction> Actions);

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
    ShadowRunReport ShadowRun,
    ImmutableDictionary<string, int> UnsupportedInteractions,
    ImmutableArray<string> InputErrors,
    ImmutableArray<ReplayArchiveSummary> Replays,
    ImmutableArray<SnapshotEvaluation> Snapshots)
{
    public double LegalRouteRate => RouteCount == 0 ? 0 : (double)LegalRouteCount / RouteCount;

    public double? ExpertTop3ConsistencyRate => AnnotatedSnapshotCount == 0
        ? null
        : (double)ExpertTop3MatchCount / AnnotatedSnapshotCount;

    public int QualifiedAnnotatedSnapshotCount => Snapshots.Count(snapshot =>
        snapshot.AnnotationQualified && snapshot.ExpertTop3Matched.HasValue);

    public int QualifiedExpertTop3MatchCount => Snapshots.Count(snapshot =>
        snapshot.AnnotationQualified && snapshot.ExpertTop3Matched == true);

    public int UnqualifiedAnnotatedSnapshotCount => Snapshots.Count(snapshot =>
        !snapshot.AnnotationQualified && snapshot.ExpertTop3Matched.HasValue);

    public double? QualifiedExpertTop3ConsistencyRate => QualifiedAnnotatedSnapshotCount == 0
        ? null
        : (double)QualifiedExpertTop3MatchCount / QualifiedAnnotatedSnapshotCount;

    public double DeadlineExpirationRate => EvaluatedSnapshotCount == 0
        ? 0
        : (double)DeadlineExpiredCount / EvaluatedSnapshotCount;

    public bool MeetsExpertAnnotationTarget => QualifiedAnnotatedSnapshotCount >= 200 &&
                                               QualifiedExpertTop3ConsistencyRate >= 0.8d;

    public bool MeetsVisibleSuggestionPrerequisites => Passed &&
                                                       EvaluatedSnapshotCount == SnapshotCount &&
                                                       RouteCount > 0 &&
                                                       LegalRouteCount == RouteCount &&
                                                       DeadlineExpiredCount == 0 &&
                                                       LatencyP95Ms < 300 &&
                                                       MeetsExpertAnnotationTarget &&
                                                       ShadowRun.MeetsAutomatedAcceptanceThresholds &&
                                                       ShadowRun.UnsupportedAnalysisCount == 0 &&
                                                       ShadowRun.UnsupportedInteractionOccurrenceCount == 0 &&
                                                       UnsupportedInteractions.IsEmpty &&
                                                       InputErrors.IsEmpty;

    public bool Passed
    {
        get
        {
            var hasOfflineInput = SnapshotCount > 0;
            var hasShadowInput = ShadowRun.StartedGameCount > 0 || ShadowRun.AnalysisCount > 0;
            var offlinePassed = !hasOfflineInput ||
                                EvaluatedSnapshotCount == SnapshotCount &&
                                RouteCount > 0 &&
                                LegalRouteCount == RouteCount;
            var shadowPassed = !hasShadowInput ||
                               ShadowRun.StartedGameCount > 0 &&
                               ShadowRun.FailedCount == 0 &&
                               ShadowRun.DuplicateRequestCount == 0 &&
                               ShadowRun.MissingRequestCount == 0 &&
                               ShadowRun.UnfinishedRequestCount == 0 &&
                               ShadowRun.VisibleSuggestionCount == 0;
            return (hasOfflineInput || hasShadowInput) && offlinePassed && shadowPassed && InputErrors.IsEmpty;
        }
    }
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
                    DisplaySource(fixture.Source),
                    fixture.Snapshot.StateId,
                    fixture.Snapshot.TurnNumber,
                    false,
                    0,
                    0,
                    0,
                    0,
                    false,
                    null,
                    false,
                    ImmutableArray<ExpertReviewCandidate>.Empty,
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
            input.Annotations.TryGetValue(fixture.Snapshot.StateId, out var annotation);
            var top3Matched = annotation is null
                ? (bool?)null
                : ExpertPrimaryMatchesAdvisorTop3(
                    result.Candidates.Select(candidate => candidate.Actions.AsEnumerable()),
                    annotation);
            var elapsedMs = Math.Max(result.Elapsed.TotalMilliseconds, stopwatch.Elapsed.TotalMilliseconds);
            evaluations.Add(new SnapshotEvaluation(
                DisplaySource(fixture.Source),
                fixture.Snapshot.StateId,
                fixture.Snapshot.TurnNumber,
                true,
                result.Candidates.Length,
                routes.Length,
                legalRouteCount,
                elapsedMs,
                fixture.Snapshot.RemainingTurnTimeMs > 0 && elapsedMs >= fixture.Snapshot.RemainingTurnTimeMs,
                top3Matched,
                annotation?.IsQualifiedForExpertTarget ?? false,
                BuildReviewCandidates(mapping.State, result.Candidates),
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
            ShadowRunReport.FromTelemetry(input.ShadowTelemetry),
            unsupported.ToImmutableDictionary(StringComparer.Ordinal),
            input.Errors,
            input.Replays,
            values);
    }

    public static bool ExpertPrimaryMatchesAdvisorTop3(
        IEnumerable<IEnumerable<RuleAction>> advisorCandidates,
        ExpertAnnotation annotation)
    {
        if (advisorCandidates is null)
            throw new ArgumentNullException(nameof(advisorCandidates));
        if (annotation is null)
            throw new ArgumentNullException(nameof(annotation));
        annotation.Validate();
        var advisorTop3 = advisorCandidates.Take(3).Select(candidate => candidate.ToArray()).ToArray();
        var expertPrimary = annotation.ExpertTop3[0];
        return advisorTop3.Any(candidate =>
            candidate.Length == expertPrimary.Actions.Length &&
            candidate.Zip(expertPrimary.Actions, (action, expected) => expected.Matches(action)).All(value => value));
    }

    private static ImmutableArray<ExpertReviewCandidate> BuildReviewCandidates(
        RuleGameState initialState,
        IEnumerable<RiskAwareRouteCandidate> candidates) => candidates.Select(candidate =>
        new ExpertReviewCandidate(
            candidate.CandidateId,
            candidate.RiskAdjustedScore,
            candidate.Confidence,
            candidate.Risk.Expected,
            candidate.Risk.P10,
            candidate.Risk.LethalProbability,
            candidate.Actions.Select(action => BuildReviewAction(
                    initialState,
                    candidate.RepresentativeRoute.State,
                    action))
                .ToImmutableArray())).ToImmutableArray();

    private static ExpertReviewAction BuildReviewAction(
        RuleGameState initialState,
        RuleGameState finalState,
        RuleAction action)
    {
        var annotation = AnnotatedAction.FromRuleAction(action);
        var sourceEntityId = action switch
        {
            UseHeroPowerAction heroPower => initialState.Player(heroPower.Side).HeroPower.EntityId,
            _ => annotation.SourceEntityId
        };
        return new ExpertReviewAction(
            annotation,
            FindCardId(initialState, finalState, sourceEntityId),
            FindCardId(initialState, finalState, annotation.TargetEntityId));
    }

    private static string? FindCardId(RuleGameState initialState, RuleGameState finalState, int? entityId)
    {
        if (entityId is not int id)
            return null;
        return FindCardId(initialState, id) ?? FindCardId(finalState, id);
    }

    private static string? FindCardId(RuleGameState state, int entityId)
    {
        foreach (var player in new[] { state.Friendly, state.Opponent })
        {
            if (player.Hero.EntityId == entityId)
                return player.Hero.CardId;
            if (player.HeroPower.EntityId == entityId)
                return player.HeroPower.CardId;
            if (player.Weapon?.EntityId == entityId)
                return player.Weapon.CardId;
            var cardId = player.Hand.FirstOrDefault(card => card.EntityId == entityId)?.CardId ??
                         player.Deck.FirstOrDefault(card => card.EntityId == entityId)?.CardId ??
                         player.Board.FirstOrDefault(card => card.EntityId == entityId)?.CardId ??
                         player.Locations.FirstOrDefault(card => card.EntityId == entityId)?.CardId ??
                         player.Graveyard.FirstOrDefault(card => card.EntityId == entityId)?.CardId;
            if (cardId is not null)
                return cardId;
        }
        return null;
    }

    private static string DisplaySource(string source)
    {
        var separator = source.IndexOf("!/", StringComparison.Ordinal);
        if (separator < 0)
            return System.IO.Path.GetFileName(source);
        return System.IO.Path.GetFileName(source.Substring(0, separator)) + source.Substring(separator);
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
