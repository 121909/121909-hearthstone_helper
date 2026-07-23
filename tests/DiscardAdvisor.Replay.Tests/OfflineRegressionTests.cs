using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using DiscardAdvisor.Domain.Snapshots;
using DiscardAdvisor.Replay;
using DiscardAdvisor.Rules.Model;
using DiscardAdvisor.Search;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Xunit;

namespace DiscardAdvisor.Replay.Tests;

public sealed class OfflineRegressionTests
{
    [Fact]
    public void Load_StandaloneSnapshotAndAnnotation()
    {
        var input = new RegressionInputLoader().Load(new[]
        {
            FixturePath("minimal-snapshot.json"),
            FixturePath("minimal-snapshot.annotation.json")
        });

        Assert.Empty(input.Errors);
        var snapshot = Assert.Single(input.Snapshots);
        Assert.Equal(
            "turn-1:bb2b37f14eb836cfe968c4e6846da086a433485cbe99c64112df303acff14cca",
            snapshot.Snapshot.StateId);
        Assert.True(input.Annotations.ContainsKey(snapshot.Snapshot.StateId));
    }

    [Fact]
    public void Load_HdtReplayReadsPowerLogAndEmbeddedDocuments()
    {
        using var directory = new TemporaryDirectory();
        var replayPath = Path.Combine(directory.Path, "sample.hdtreplay");
        CreateReplay(
            replayPath,
            ("output_log.txt", "line one\r\nline two\r\n"),
            ("discard-advisor/snapshots/turn-1.snapshot.json", File.ReadAllText(FixturePath("minimal-snapshot.json"))),
            ("discard-advisor/annotations/turn-1.annotation.json", File.ReadAllText(FixturePath("minimal-snapshot.annotation.json"))));

        var input = new RegressionInputLoader().Load(new[] { replayPath });

        Assert.Empty(input.Errors);
        var replay = Assert.Single(input.Replays);
        Assert.Equal(2, replay.PowerLogLineCount);
        Assert.Equal(1, replay.SnapshotCount);
        Assert.Single(input.Snapshots);
        Assert.Single(input.Annotations);
    }

    [Fact]
    public void Load_HdtReplayReadsNamedSidecarDirectory()
    {
        using var directory = new TemporaryDirectory();
        var replayPath = Path.Combine(directory.Path, "sample.hdtreplay");
        CreateReplay(replayPath, ("output_log.txt", "line one\n"));
        var sidecar = Path.Combine(directory.Path, "sample.snapshots");
        Directory.CreateDirectory(sidecar);
        File.Copy(FixturePath("minimal-snapshot.json"), Path.Combine(sidecar, "turn-1.snapshot.json"));

        var input = new RegressionInputLoader().Load(new[] { replayPath });

        Assert.Empty(input.Errors);
        Assert.Single(input.Snapshots);
        Assert.Equal(1, Assert.Single(input.Replays).SnapshotCount);
    }

    [Fact]
    public void Load_RejectsReplayWithoutPowerLog()
    {
        using var directory = new TemporaryDirectory();
        var replayPath = Path.Combine(directory.Path, "invalid.hdtreplay");
        CreateReplay(replayPath, ("other.txt", "not a replay log"));

        var input = new RegressionInputLoader().Load(new[] { replayPath });

        Assert.Empty(input.Snapshots);
        Assert.Contains(input.Errors, error => error.Contains("output_log.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_AcceptsRecordedActionsWithoutRestoringHiddenFields()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "recorded.snapshot.json");
        var json = JObject.Parse(File.ReadAllText(FixturePath("minimal-snapshot.json")));
        json["actionsThisTurn"] = new JArray(new JObject { ["actionType"] = "END_TURN" });
        json["stateId"] = CalculateStateId(json);
        File.WriteAllText(path, json.ToString());

        var input = new RegressionInputLoader().Load(new[] { path });

        Assert.Empty(input.Errors);
        Assert.Equal("END_TURN", Assert.Single(input.Snapshots).Snapshot.ActionsThisTurn.Single().ActionType);
    }

    [Fact]
    public void Load_RejectsSnapshotWhoseStateIdDoesNotMatchItsContents()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "modified.snapshot.json");
        var json = JObject.Parse(File.ReadAllText(FixturePath("minimal-snapshot.json")));
        json["turnNumber"] = json.Value<int>("turnNumber") + 1;
        File.WriteAllText(path, json.ToString());

        var input = new RegressionInputLoader().Load(new[] { path });

        Assert.Empty(input.Snapshots);
        Assert.Contains(input.Errors, error => error.Contains("does not match calculated state_id", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_ReplaysFixtureAndMatchesExpertTop3()
    {
        var input = new RegressionInputLoader().Load(new[]
        {
            FixturePath("minimal-snapshot.json"),
            FixturePath("minimal-snapshot.annotation.json")
        });
        var runner = new OfflineRegressionRunner(utcNow: () => new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero));

        var report = runner.Run(input, new OfflineRegressionOptions(MaximumActions: 1, TimeBudgetMs: 500));

        Assert.True(report.Passed);
        Assert.Equal(1, report.EvaluatedSnapshotCount);
        Assert.Equal(report.RouteCount, report.LegalRouteCount);
        Assert.True(report.RouteCount > 0);
        Assert.Equal(1, report.ExpertTop3MatchCount);
        Assert.Equal(1d, report.ExpertTop3ConsistencyRate);
        Assert.Equal(1, report.QualifiedAnnotatedSnapshotCount);
        Assert.Equal(1, report.QualifiedExpertTop3MatchCount);
        Assert.Equal(1d, report.QualifiedExpertTop3ConsistencyRate);
        Assert.False(report.MeetsVisibleSuggestionPrerequisites);
        Assert.Empty(report.UnsupportedInteractions);
    }

    [Fact]
    public void ExpertConsistencyUsesThePrimaryRouteInsteadOfSetIntersection()
    {
        var primaryNotSuggested = new ExpertAnnotation(
            "1.0.0",
            "turn-1:test",
            new[]
            {
                new AnnotatedRoute("primary", new[] { new AnnotatedAction("PLAY_CARD", 1) }),
                new AnnotatedRoute("alternative", new[] { new AnnotatedAction("END_TURN") })
            });
        var advisorTop3 = new IEnumerable<RuleAction>[]
        {
            new RuleAction[] { new EndTurnAction(PlayerSide.Friendly) }
        };

        Assert.False(OfflineRegressionRunner.ExpertPrimaryMatchesAdvisorTop3(advisorTop3, primaryNotSuggested));

        var primarySuggested = new ExpertAnnotation(
            "1.0.0",
            "turn-1:test",
            new[]
            {
                new AnnotatedRoute("primary", new[] { new AnnotatedAction("END_TURN") }),
                new AnnotatedRoute("alternative", new[] { new AnnotatedAction("PLAY_CARD", 1) })
            });
        Assert.True(OfflineRegressionRunner.ExpertPrimaryMatchesAdvisorTop3(advisorTop3, primarySuggested));
    }

    [Fact]
    public void QualifiedExpertAnnotationRequiresReviewerProvenance()
    {
        var routes = new[] { new AnnotatedRoute("primary", new[] { new AnnotatedAction("END_TURN") }) };
        var legacy = new ExpertAnnotation(ExpertAnnotation.LegacyProtocolVersion, "turn-1:legacy", routes);
        var qualified = new ExpertAnnotation(
            ExpertAnnotation.CurrentProtocolVersion,
            "turn-1:qualified",
            routes,
            "expert-a",
            DateTimeOffset.Parse("2026-07-22T00:00:00Z"));
        var missingProvenance = new ExpertAnnotation(ExpertAnnotation.CurrentProtocolVersion, "turn-1:missing", routes);
        var invalidReviewer = new ExpertAnnotation(
            ExpertAnnotation.CurrentProtocolVersion,
            "turn-1:invalid-reviewer",
            routes,
            "expert reviewer",
            DateTimeOffset.Parse("2026-07-22T00:00:00Z"));

        legacy.Validate();
        qualified.Validate();
        Assert.False(legacy.IsQualifiedForExpertTarget);
        Assert.True(qualified.IsQualifiedForExpertTarget);
        Assert.Throws<InvalidOperationException>(missingProvenance.Validate);
        Assert.Throws<InvalidOperationException>(invalidReviewer.Validate);
    }

    [Fact]
    public void Run_ReportsLegacyAnnotationsSeparatelyFromQualifiedEvidence()
    {
        using var directory = new TemporaryDirectory();
        var snapshotPath = Path.Combine(directory.Path, "snapshot.snapshot.json");
        var annotationPath = Path.Combine(directory.Path, "legacy.annotation.json");
        File.Copy(FixturePath("minimal-snapshot.json"), snapshotPath);
        var legacy = JObject.Parse(File.ReadAllText(FixturePath("minimal-snapshot.annotation.json")));
        legacy["protocolVersion"] = ExpertAnnotation.LegacyProtocolVersion;
        legacy.Remove("reviewerId");
        legacy.Remove("reviewedAtUtc");
        File.WriteAllText(annotationPath, legacy.ToString());

        var input = new RegressionInputLoader().Load(new[] { snapshotPath, annotationPath });
        var report = new OfflineRegressionRunner().Run(input, new OfflineRegressionOptions(MaximumActions: 1, TimeBudgetMs: 500));

        Assert.Empty(input.Errors);
        Assert.Equal(1, report.AnnotatedSnapshotCount);
        Assert.Equal(0, report.QualifiedAnnotatedSnapshotCount);
        Assert.Equal(1, report.UnqualifiedAnnotatedSnapshotCount);
        Assert.True(report.MeetsExpertAnnotationTarget);
        Assert.False(report.ExpertAnnotationsRequired);
        var paths = new RegressionReportWriter().Write(report, directory.Path);
        var review = JObject.Parse(File.ReadAllText(paths.ExpertReviewPath));
        Assert.Single(Assert.IsType<JArray>(review["pending"]));
    }

    [Fact]
    public void Run_AggregatesOnlyShadowModeTelemetry()
    {
        var input = new RegressionInputLoader().Load(new[] { FixturePath("shadow-run.jsonl") });

        var report = new OfflineRegressionRunner().Run(input);

        Assert.Empty(input.Errors);
        Assert.True(report.Passed);
        Assert.Equal(2, report.ShadowRun.StartedGameCount);
        Assert.Equal(1, report.ShadowRun.CompletedGameCount);
        Assert.Equal(1, report.ShadowRun.CompletedGameWithPublishedAnalysisCount);
        Assert.Equal(0, report.ShadowRun.CompletedGameWithoutPublishedAnalysisCount);
        Assert.Equal(2, report.ShadowRun.AnalysisCount);
        Assert.Equal(2, report.ShadowRun.RequestCount);
        Assert.Equal(1, report.ShadowRun.PublishedCount);
        Assert.Equal(1, report.ShadowRun.SupersededCount);
        Assert.Equal(0.5d, report.ShadowRun.SupersededRate);
        Assert.Equal(250d, report.ShadowRun.LatencyP95Ms);
        Assert.Equal(0, report.ShadowRun.VisibleSuggestionCount);
        Assert.Equal(0, report.ShadowRun.DuplicateRequestCount);
        Assert.Equal(0, report.ShadowRun.MissingRequestCount);
        Assert.Equal(0, report.ShadowRun.UnfinishedRequestCount);
        Assert.Equal(1, report.ShadowRun.RunCount);
        Assert.Equal(1, report.ShadowRun.VersionCohortCount);
        Assert.Equal(0, report.ShadowRun.MissingVersionMetadataGameCount);
        var cohort = Assert.Single(report.ShadowRun.VersionCohorts);
        Assert.Equal(("0.4.11", "0.3.3"), (cohort.PluginVersion, cohort.RuleSetVersion));
        Assert.False(report.ShadowRun.MeetsAutomatedAcceptanceThresholds);
    }

    [Fact]
    public void ShadowReportCanIgnoreHistoricalPluginCohorts()
    {
        var current = BuildCompletedShadowRunTelemetry(5);
        var oldGameId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        var oldTimestamp = DateTimeOffset.Parse("2026-07-21T00:00:00Z");
        var old = new ShadowRunTelemetry(
            1,
            ImmutableArray.Create(new ShadowGameSession(
                oldGameId,
                "shadow",
                true,
                true,
                true,
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "0.4.9",
                "0.3.3")),
            ImmutableArray.Create(new ShadowAdvisorRequestObservation(
                oldTimestamp,
                1,
                oldGameId,
                "old-state",
                "shadow")),
            ImmutableArray.Create(Analysis(oldTimestamp.AddSeconds(1), 2, oldGameId, "old-state", "Published")),
            ImmutableArray<string>.Empty);
        var telemetry = current with
        {
            LogFileCount = 2,
            Games = current.Games.AddRange(old.Games),
            Requests = current.Requests.AddRange(old.Requests),
            Analyses = current.Analyses.AddRange(old.Analyses)
        };

        var report = ShadowRunReport.FromTelemetry(telemetry, "0.4.11", "0.3.3");

        Assert.Equal(5, report.CompletedGameWithPublishedAnalysisCount);
        Assert.Equal(5, report.AnalysisCount);
        Assert.Equal(1, report.VersionCohortCount);
        Assert.Equal(1, report.IgnoredVersionGameCount);
        Assert.Equal("0.4.11", report.TargetPluginVersion);
        Assert.Equal("0.3.3", report.TargetRuleSetVersion);
    }

    [Fact]
    public void ShadowReportDistinguishesLegitimateRetryFromConcurrentDuplicate()
    {
        var gameId = Guid.Parse("5e8908b5-9a47-47de-bcfe-38d5807fb984");
        var start = DateTimeOffset.Parse("2026-07-22T00:00:00Z");
        var requests = ImmutableArray.Create(
            new ShadowAdvisorRequestObservation(start, 1, gameId, "state-a", "shadow"),
            new ShadowAdvisorRequestObservation(start.AddSeconds(2), 3, gameId, "state-a", "shadow"),
            new ShadowAdvisorRequestObservation(start.AddSeconds(4), 5, gameId, "state-b", "shadow"),
            new ShadowAdvisorRequestObservation(start.AddSeconds(4.5), 6, gameId, "state-b", "shadow"));
        var analyses = ImmutableArray.Create(
            Analysis(start.AddSeconds(1), 2, gameId, "state-a", "Superseded"),
            Analysis(start.AddSeconds(3), 4, gameId, "state-a", "Published"),
            Analysis(start.AddSeconds(5), 7, gameId, "state-b", "Superseded"),
            Analysis(start.AddSeconds(6), 8, gameId, "state-b", "Published", "UnsupportedInteraction", 2));
        var telemetry = new ShadowRunTelemetry(
            1,
            ImmutableArray.Create(new ShadowGameSession(gameId, "shadow", true, true, true)),
            requests,
            analyses,
            ImmutableArray<string>.Empty);

        var report = ShadowRunReport.FromTelemetry(telemetry);

        Assert.Equal(1, report.DuplicateRequestCount);
        Assert.Equal(0, report.MissingRequestCount);
        Assert.Equal(0, report.UnfinishedRequestCount);
        Assert.Equal(1, report.UnsupportedAnalysisCount);
        Assert.Equal(2, report.UnsupportedInteractionOccurrenceCount);
    }

    [Fact]
    public void ShadowReportRejectsTerminalAnalysisWithoutRequestEvidence()
    {
        var gameId = Guid.Parse("5e8908b5-9a47-47de-bcfe-38d5807fb984");
        var timestamp = DateTimeOffset.Parse("2026-07-22T00:00:00Z");
        var telemetry = new ShadowRunTelemetry(
            1,
            ImmutableArray.Create(new ShadowGameSession(gameId, "shadow", true, true, true)),
            ImmutableArray<ShadowAdvisorRequestObservation>.Empty,
            ImmutableArray.Create(Analysis(timestamp, 1, gameId, "state-a", "Published")),
            ImmutableArray<string>.Empty);

        var report = ShadowRunReport.FromTelemetry(telemetry);

        Assert.Equal(1, report.MissingRequestCount);
        Assert.False(report.MeetsAutomatedAcceptanceThresholds);
    }

    [Fact]
    public void ShadowAcceptanceCountsOnlyCompletedGamesWithPublishedAnalyses()
    {
        var incompleteEvidence = ShadowRunReport.FromTelemetry(BuildCompletedShadowRunTelemetry(4));

        Assert.Equal(5, incompleteEvidence.CompletedGameCount);
        Assert.Equal(4, incompleteEvidence.CompletedGameWithPublishedAnalysisCount);
        Assert.Equal(1, incompleteEvidence.CompletedGameWithoutPublishedAnalysisCount);
        Assert.False(incompleteEvidence.MeetsAutomatedAcceptanceThresholds);

        var acceptedEvidence = ShadowRunReport.FromTelemetry(BuildCompletedShadowRunTelemetry(5));

        Assert.Equal(5, acceptedEvidence.CompletedGameWithPublishedAnalysisCount);
        Assert.Equal(0, acceptedEvidence.CompletedGameWithoutPublishedAnalysisCount);
        Assert.True(acceptedEvidence.MeetsAutomatedAcceptanceThresholds);
    }

    [Fact]
    public void VisibleSuggestionGateRequiresCompleteOfflineAndShadowEvidence()
    {
        var qualifiedSnapshots = ImmutableArray<SnapshotEvaluation>.Empty;
        var shadow = new ShadowRunReport(
            LogFileCount: 1,
            StartedGameCount: 5,
            CompletedGameCount: 5,
            CompletedGameWithPublishedAnalysisCount: 5,
            RequestCount: 5,
            AnalysisCount: 5,
            PublishedCount: 5,
            SupersededCount: 0,
            CancelledCount: 0,
            FailedCount: 0,
            DuplicateRequestCount: 0,
            MissingRequestCount: 0,
            UnfinishedRequestCount: 0,
            VisibleSuggestionCount: 0,
            UnsupportedAnalysisCount: 0,
            UnsupportedInteractionOccurrenceCount: 0,
            RunCount: 1,
            VersionCohortCount: 1,
            MissingVersionMetadataGameCount: 0,
            VersionCohorts: ImmutableArray.Create(new ShadowVersionCohort("0.4.11", "0.3.3", 5, 5, 5, 5)),
            LatencyP50Ms: 100,
            LatencyP95Ms: 200,
            LatencyMaximumMs: 250);
        var report = new OfflineRegressionReport(
            DateTimeOffset.Parse("2026-07-22T00:00:00Z"),
            new OfflineRegressionOptions(),
            ReplayCount: 5,
            SnapshotCount: 0,
            EvaluatedSnapshotCount: 0,
            AnnotatedSnapshotCount: 0,
            ExpertTop3MatchCount: 0,
            RouteCount: 1,
            LegalRouteCount: 1,
            DeadlineExpiredCount: 0,
            LatencyP50Ms: 100,
            LatencyP95Ms: 200,
            LatencyMaximumMs: 250,
            ShadowRun: shadow,
            UnsupportedInteractions: ImmutableDictionary<string, int>.Empty,
            InputErrors: ImmutableArray<string>.Empty,
            Replays: ImmutableArray<ReplayArchiveSummary>.Empty,
            Snapshots: qualifiedSnapshots);

        Assert.True(report.MeetsVisibleSuggestionPrerequisites);
        Assert.True((report with { AnnotatedSnapshotCount = 0 }).MeetsVisibleSuggestionPrerequisites);
        Assert.False((report with { LatencyP95Ms = 300 }).MeetsVisibleSuggestionPrerequisites);
        Assert.False((report with
        {
            ShadowRun = shadow with { CompletedGameWithPublishedAnalysisCount = 4 }
        }).MeetsVisibleSuggestionPrerequisites);
        Assert.False((report with
        {
            ShadowRun = shadow with { UnsupportedAnalysisCount = 1 }
        }).MeetsVisibleSuggestionPrerequisites);
    }

    [Fact]
    public void RouteLegalityVerifier_RejectsInvalidAction()
    {
        var input = new RegressionInputLoader().Load(new[] { FixturePath("minimal-snapshot.json") });
        var state = Assert.IsType<RuleGameState>(new SnapshotRuleStateMapper().Map(Assert.Single(input.Snapshots).Snapshot).State);

        var legal = new RouteLegalityVerifier().IsLegal(
            state,
            new RuleAction[] { new AttackAction(PlayerSide.Friendly, 999999, state.Opponent.Hero.EntityId) },
            0x5EED);

        Assert.False(legal);
    }

    [Fact]
    public void ReportWriter_WritesJsonAndMarkdown()
    {
        using var directory = new TemporaryDirectory();
        var input = new RegressionInputLoader().Load(new[] { FixturePath("minimal-snapshot.json") });
        var report = new OfflineRegressionRunner().Run(
            input,
            new OfflineRegressionOptions(MaximumActions: 1, TimeBudgetMs: 500));

        var paths = new RegressionReportWriter().Write(report, directory.Path);

        Assert.True(File.Exists(paths.JsonPath));
        Assert.True(File.Exists(paths.MarkdownPath));
        Assert.True(File.Exists(paths.ExpertReviewPath));
        Assert.Contains("Legal routes", File.ReadAllText(paths.MarkdownPath), StringComparison.Ordinal);
        Assert.Contains("Visible-suggestion prerequisites", File.ReadAllText(paths.MarkdownPath), StringComparison.Ordinal);
        var reportJson = JObject.Parse(File.ReadAllText(paths.JsonPath));
        Assert.False(reportJson.Value<bool>("meetsVisibleSuggestionPrerequisites"));
        var review = JObject.Parse(File.ReadAllText(paths.ExpertReviewPath));
        Assert.Equal("BLIND_ROUTE_RANKING", review.Value<string>("reviewMethod"));
        var pending = Assert.IsType<JArray>(review["pending"]);
        var item = Assert.IsType<JObject>(Assert.Single(pending));
        Assert.Equal("minimal-snapshot.json", item.Value<string>("source"));
        Assert.NotNull(item["customRouteTemplate"]);
        var candidates = Assert.IsType<JArray>(item["options"]);
        var candidate = Assert.IsType<JObject>(Assert.Single(candidates));
        Assert.Null(candidate["candidateId"]);
        Assert.Null(candidate["riskAdjustedScore"]);
        var actions = Assert.IsType<JArray>(candidate["actions"]);
        var action = Assert.IsType<JObject>(Assert.Single(actions));
        Assert.Equal("END_TURN", action["annotation"]?.Value<string>("kind"));

        var annotationPath = new ExpertAnnotationDraftWriter(() => DateTimeOffset.Parse("2026-07-22T00:00:00Z")).Write(
            paths.ExpertReviewPath,
            item.Value<string>("stateId")!,
            new[] { candidate.Value<string>("reviewOptionId")! },
            "fixture-reviewer",
            Path.Combine(directory.Path, "annotations"));
        var annotation = JObject.Parse(File.ReadAllText(annotationPath));
        Assert.Equal(item.Value<string>("stateId"), annotation.Value<string>("stateId"));
        Assert.Equal(ExpertAnnotation.CurrentProtocolVersion, annotation.Value<string>("protocolVersion"));
        Assert.Equal("fixture-reviewer", annotation.Value<string>("reviewerId"));
        Assert.Equal(
            DateTime.Parse("2026-07-22T00:00:00Z", null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            annotation.Value<DateTime>("reviewedAtUtc").ToUniversalTime());
        Assert.Equal(
            "END_TURN",
            annotation["expertTop3"]?[0]?["actions"]?[0]?.Value<string>("kind"));
        Assert.Throws<IOException>(() => new ExpertAnnotationDraftWriter().Write(
            paths.ExpertReviewPath,
            item.Value<string>("stateId")!,
            new[] { candidate.Value<string>("reviewOptionId")! },
            "fixture-reviewer",
            Path.Combine(directory.Path, "annotations")));

        var legacyPath = new ExpertAnnotationDraftWriter().Write(
            paths.ExpertReviewPath,
            item.Value<string>("stateId")!,
            new[] { candidate.Value<string>("reviewOptionId")! },
            Path.Combine(directory.Path, "legacy-annotations"));
        var legacyAnnotation = JObject.Parse(File.ReadAllText(legacyPath));
        Assert.Equal(ExpertAnnotation.LegacyProtocolVersion, legacyAnnotation.Value<string>("protocolVersion"));
        Assert.Equal(JTokenType.Null, legacyAnnotation["reviewerId"]?.Type);
    }

    [Fact]
    public void AnnotationValidationRejectsIncompleteActionFields()
    {
        var annotation = new ExpertAnnotation(
            "1.0.0",
            "turn-1:test",
            new[]
            {
                new AnnotatedRoute("invalid", new[] { new AnnotatedAction("ATTACK", SourceEntityId: 10) })
            });

        Assert.Throws<InvalidOperationException>(annotation.Validate);
    }

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static ShadowAnalysisObservation Analysis(
        DateTimeOffset timestamp,
        long sequence,
        Guid gameId,
        string stateId,
        string disposition,
        string status = "Ready",
        int unsupportedInteractionCount = 0) => new(
        timestamp,
        sequence,
        gameId,
        stateId,
        "shadow",
        disposition,
        status,
        100,
        80,
        3,
        unsupportedInteractionCount,
        false);

    private static ShadowRunTelemetry BuildCompletedShadowRunTelemetry(int publishedGameCount)
    {
        var start = DateTimeOffset.Parse("2026-07-22T00:00:00Z");
        const int gameCount = 5;
        var games = ImmutableArray.CreateBuilder<ShadowGameSession>(gameCount);
        var requests = ImmutableArray.CreateBuilder<ShadowAdvisorRequestObservation>(gameCount);
        var analyses = ImmutableArray.CreateBuilder<ShadowAnalysisObservation>(gameCount);
        for (var index = 0; index < gameCount; index++)
        {
            var gameId = new Guid(index + 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            var timestamp = start.AddMinutes(index);
            var stateId = $"state-{index}";
            games.Add(new ShadowGameSession(
                gameId,
                "shadow",
                true,
                true,
                true,
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "0.4.11",
                "0.3.3"));
            requests.Add(new ShadowAdvisorRequestObservation(timestamp, index * 2 + 1, gameId, stateId, "shadow"));
            analyses.Add(Analysis(
                timestamp.AddMilliseconds(100),
                index * 2 + 2,
                gameId,
                stateId,
                index < publishedGameCount ? "Published" : "Superseded"));
        }
        return new ShadowRunTelemetry(
            1,
            games.ToImmutable(),
            requests.ToImmutable(),
            analyses.ToImmutable(),
            ImmutableArray<string>.Empty);
    }

    private static string CalculateStateId(JObject json)
    {
        var serializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            MissingMemberHandling = MissingMemberHandling.Error,
            NullValueHandling = NullValueHandling.Include,
            Converters = { new TestSnapshotActionJsonConverter() }
        });
        var snapshot = json.ToObject<GameSnapshot>(serializer) ??
                       throw new InvalidOperationException("Test Snapshot JSON produced null.");
        return SnapshotStateId.Calculate(snapshot);
    }

    private static void CreateReplay(string path, params (string Name, string Contents)[] entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Name);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(item.Contents);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "discard-advisor-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, true);
    }

    private sealed class TestSnapshotActionJsonConverter : JsonConverter
    {
        public override bool CanWrite => false;

        public override bool CanConvert(Type objectType) => objectType == typeof(SnapshotAction);

        public override object ReadJson(
            JsonReader reader,
            Type objectType,
            object? existingValue,
            JsonSerializer serializer)
        {
            var value = JObject.Load(reader);
            var actionType = value.Value<string>("actionType") ??
                             throw new JsonSerializationException("Recorded snapshot action requires actionType.");
            return new TestSnapshotAction(actionType);
        }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) =>
            throw new NotSupportedException();
    }

    private sealed class TestSnapshotAction : SnapshotAction
    {
        public TestSnapshotAction(string actionType)
            : base(actionType)
        {
        }
    }
}
