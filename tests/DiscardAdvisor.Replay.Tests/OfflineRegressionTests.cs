using System;
using System.IO;
using System.IO.Compression;
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
            "turn-1:458d5075d07ea0c3eb58bbf55f80627e0b672925d47e538510f752ffaf764a87",
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
        Assert.Empty(report.UnsupportedInteractions);
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
        Assert.Equal(2, report.ShadowRun.AnalysisCount);
        Assert.Equal(1, report.ShadowRun.PublishedCount);
        Assert.Equal(1, report.ShadowRun.SupersededCount);
        Assert.Equal(0.5d, report.ShadowRun.SupersededRate);
        Assert.Equal(250d, report.ShadowRun.LatencyP95Ms);
        Assert.Equal(0, report.ShadowRun.VisibleSuggestionCount);
        Assert.Equal(0, report.ShadowRun.DuplicateRequestCount);
        Assert.False(report.ShadowRun.MeetsAutomatedAcceptanceThresholds);
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
        var review = JObject.Parse(File.ReadAllText(paths.ExpertReviewPath));
        var pending = Assert.IsType<JArray>(review["pending"]);
        var item = Assert.IsType<JObject>(Assert.Single(pending));
        Assert.Equal("minimal-snapshot.json", item.Value<string>("source"));
        var candidates = Assert.IsType<JArray>(item["candidates"]);
        var candidate = Assert.IsType<JObject>(Assert.Single(candidates));
        var actions = Assert.IsType<JArray>(candidate["actions"]);
        var action = Assert.IsType<JObject>(Assert.Single(actions));
        Assert.Equal("END_TURN", action["annotation"]?.Value<string>("kind"));
    }

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

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
