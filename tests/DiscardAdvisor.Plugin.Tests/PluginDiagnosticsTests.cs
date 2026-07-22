using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DiscardAdvisor.Domain;
using DiscardAdvisor.Domain.Snapshots;
using Json.Schema;
using Xunit;

namespace DiscardAdvisor.Plugin.Tests;

public sealed class PluginDiagnosticsTests
{
    [Fact]
    public void SettingsUseExperimentalByDefaultAndFailClosedToShadow()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            Assert.Equal(PluginPresentationMode.Experimental, PluginSettings.Load(path).PresentationMode);

            File.WriteAllText(path, "{ \"mode\": \"shadow\" }");
            var shadow = PluginSettings.Load(path);
            Assert.Equal(PluginPresentationMode.Shadow, shadow.PresentationMode);
            Assert.False(shadow.ShowOverlay);

            File.WriteAllText(path, "{ invalid json");
            Assert.Equal(PluginPresentationMode.Shadow, PluginSettings.Load(path).PresentationMode);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ExportsPrivacyFilteredSnapshotAtomically()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var snapshot = CreateSnapshot();
            var exporter = new SnapshotFixtureExporter(directory);

            var path = exporter.Export(snapshot);
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            var buildOptions = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
            JsonSchema.FromText(File.ReadAllText(Path.Combine("schemas", "common.schema.json")), buildOptions);
            JsonSchema.FromText(File.ReadAllText(Path.Combine("schemas", "action.schema.json")), buildOptions);
            var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine("schemas", "snapshot.schema.json")), buildOptions);
            var schemaResult = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

            Assert.Equal(snapshot.StateId, document.RootElement.GetProperty("stateId").GetString());
            Assert.True(schemaResult.IsValid);
            Assert.DoesNotContain("HIDDEN_HAND_CARD", json, StringComparison.Ordinal);
            Assert.DoesNotContain("Player#1234", json, StringComparison.Ordinal);
            Assert.DoesNotContain(':', Path.GetFileName(path));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void WritesRedactedRotatingJsonLines()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var fixtureDirectory = Path.Combine(directory, "fixtures");
            var store = new RedactedDiagnosticStore(
                directory,
                maxBytes: 1,
                retainedFiles: 2,
                utcNow: () => DateTimeOffset.Parse("2026-07-22T00:00:00Z"),
                fixtureExporter: new SnapshotFixtureExporter(fixtureDirectory));
            var snapshot = CreateSnapshot();

            store.RecordSnapshot(snapshot);
            store.RecordError("capture_failed", new InvalidOperationException("account-secret C:\\Users\\secret"));

            var currentLog = File.ReadAllText(Path.Combine(directory, "discard-advisor.jsonl"));
            var rotatedLog = File.ReadAllText(Path.Combine(directory, "discard-advisor.jsonl.1"));
            var allLogs = currentLog + rotatedLog;
            Assert.Contains("snapshot_ready", allLogs, StringComparison.Ordinal);
            Assert.Contains("capture_failed", allLogs, StringComparison.Ordinal);
            Assert.Contains("InvalidOperationException", allLogs, StringComparison.Ordinal);
            Assert.DoesNotContain("account-secret", allLogs, StringComparison.Ordinal);
            Assert.DoesNotContain("C:\\Users\\secret", allLogs, StringComparison.Ordinal);
            Assert.Single(Directory.GetFiles(fixtureDirectory, "*.snapshot.json"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void WritesShadowSessionAndAnalysisTelemetry()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var gameId = Guid.Parse("5e8908b5-9a47-47de-bcfe-38d5807fb984");
            var store = new RedactedDiagnosticStore(
                directory,
                utcNow: () => DateTimeOffset.Parse("2026-07-22T00:00:00Z"),
                sessionMode: "shadow");

            store.RecordGameStarted(gameId);
            store.RecordAdvisorAnalysis(new AdvisorAnalysisDiagnostic(
                gameId,
                "turn-3:fixture",
                AdvisorAnalysisDisposition.Superseded,
                PluginAdvisorStatus.Ready,
                123.5,
                100.25,
                5,
                0));
            store.RecordGameEnded(gameId, completed: true);

            var log = File.ReadAllText(Path.Combine(directory, "discard-advisor.jsonl"));
            Assert.Contains("game_started", log, StringComparison.Ordinal);
            Assert.Contains("game_ended", log, StringComparison.Ordinal);
            Assert.Contains("\"completed\":true", log, StringComparison.Ordinal);
            Assert.Contains("advisor_analysis", log, StringComparison.Ordinal);
            Assert.Contains("Superseded", log, StringComparison.Ordinal);
            Assert.Contains("\"mode\":\"shadow\"", log, StringComparison.Ordinal);
            Assert.Contains("\"suggestionVisible\":false", log, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task QueuesDiagnosticsOffTheCallerAndPreservesEventOrder()
    {
        var inner = new BlockingDiagnostics();
        var queued = new QueuedPluginDiagnostics(inner);

        queued.RecordGameStarted(Guid.Empty);
        Assert.True(inner.Started.Wait(TimeSpan.FromSeconds(2)));
        queued.RecordGameEnded(Guid.Empty, completed: true);
        Assert.Empty(inner.Events);

        inner.Release.Set();
        await queued.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new[] { "started", "ended" }, inner.Events);
    }

    private static GameSnapshot CreateSnapshot()
    {
        var friendly = GameSnapshotBuilderTests.CreateFriendly(Array.Empty<HandCardSnapshot>());
        return new GameSnapshotBuilder().Build(GameSnapshotBuilderTests.CreateObservation(friendly));
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "discard-advisor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class BlockingDiagnostics : IPluginDiagnostics
    {
        public ManualResetEventSlim Started { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public ConcurrentQueue<string> Events { get; } = new();

        public void RecordGameStarted(Guid gameId)
        {
            Started.Set();
            Release.Wait();
            Events.Enqueue("started");
        }

        public void RecordGameEnded(Guid gameId, bool completed) => Events.Enqueue("ended");

        public void RecordGateDecision(GateDecision decision)
        {
        }

        public void RecordSnapshot(GameSnapshot snapshot)
        {
        }

        public void RecordAdvisorAnalysis(AdvisorAnalysisDiagnostic analysis)
        {
        }

        public void RecordError(string code, Exception exception)
        {
        }
    }
}
