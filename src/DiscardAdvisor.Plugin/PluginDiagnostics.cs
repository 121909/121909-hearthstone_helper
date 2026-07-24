using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DiscardAdvisor.Domain;
using DiscardAdvisor.Domain.Snapshots;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace DiscardAdvisor.Plugin;

public interface IPluginDiagnostics
{
    void RecordGameStarted(Guid gameId);

    void RecordGameEnded(Guid gameId, bool completed);

    void RecordGateDecision(GateDecision decision);

    void RecordSnapshotCaptureSkipped(SnapshotCaptureFailure failure);

    void RecordSnapshot(GameSnapshot snapshot);

    void RecordAdvisorRequest(AdvisorRequestDiagnostic request);

    void RecordAdvisorAnalysis(AdvisorAnalysisDiagnostic analysis);

    void RecordError(string code, Exception exception, string? stateId = null);
}

public enum AdvisorAnalysisDisposition
{
    Published,
    Superseded,
    Cancelled,
    Failed
}

public sealed class AdvisorRequestDiagnostic
{
    public AdvisorRequestDiagnostic(Guid gameId, string stateId)
    {
        if (string.IsNullOrWhiteSpace(stateId))
            throw new ArgumentException("A state id is required.", nameof(stateId));
        GameId = gameId;
        StateId = stateId;
    }

    public Guid GameId { get; }
    public string StateId { get; }
}

public sealed class AdvisorAnalysisDiagnostic
{
    public AdvisorAnalysisDiagnostic(
        Guid gameId,
        string stateId,
        AdvisorAnalysisDisposition disposition,
        PluginAdvisorStatus status,
        double elapsedMs,
        double localSearchElapsedMs,
        int candidateCount,
        int unsupportedInteractionCount)
    {
        if (string.IsNullOrWhiteSpace(stateId))
            throw new ArgumentException("A state id is required.", nameof(stateId));
        if (elapsedMs < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedMs));
        if (localSearchElapsedMs < 0)
            throw new ArgumentOutOfRangeException(nameof(localSearchElapsedMs));
        if (candidateCount < 0)
            throw new ArgumentOutOfRangeException(nameof(candidateCount));
        if (unsupportedInteractionCount < 0)
            throw new ArgumentOutOfRangeException(nameof(unsupportedInteractionCount));

        GameId = gameId;
        StateId = stateId;
        Disposition = disposition;
        Status = status;
        ElapsedMs = elapsedMs;
        LocalSearchElapsedMs = localSearchElapsedMs;
        CandidateCount = candidateCount;
        UnsupportedInteractionCount = unsupportedInteractionCount;
    }

    public Guid GameId { get; }
    public string StateId { get; }
    public AdvisorAnalysisDisposition Disposition { get; }
    public PluginAdvisorStatus Status { get; }
    public double ElapsedMs { get; }
    public double LocalSearchElapsedMs { get; }
    public int CandidateCount { get; }
    public int UnsupportedInteractionCount { get; }
}

public sealed class NullPluginDiagnostics : IPluginDiagnostics
{
    public static NullPluginDiagnostics Instance { get; } = new();

    private NullPluginDiagnostics()
    {
    }

    public void RecordGameStarted(Guid gameId)
    {
    }

    public void RecordGameEnded(Guid gameId, bool completed)
    {
    }

    public void RecordGateDecision(GateDecision decision)
    {
    }

    public void RecordSnapshotCaptureSkipped(SnapshotCaptureFailure failure)
    {
    }

    public void RecordSnapshot(GameSnapshot snapshot)
    {
    }

    public void RecordAdvisorRequest(AdvisorRequestDiagnostic request)
    {
    }

    public void RecordAdvisorAnalysis(AdvisorAnalysisDiagnostic analysis)
    {
    }

    public void RecordError(string code, Exception exception, string? stateId = null)
    {
    }
}

public sealed class QueuedPluginDiagnostics : IPluginDiagnostics
{
    private readonly object _gate = new();
    private readonly IPluginDiagnostics _inner;
    private Task _tail = Task.CompletedTask;

    public QueuedPluginDiagnostics(IPluginDiagnostics inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public void RecordGameStarted(Guid gameId) => Enqueue(() => _inner.RecordGameStarted(gameId));

    public void RecordGameEnded(Guid gameId, bool completed) =>
        Enqueue(() => _inner.RecordGameEnded(gameId, completed));

    public void RecordGateDecision(GateDecision decision) => Enqueue(() => _inner.RecordGateDecision(decision));

    public void RecordSnapshotCaptureSkipped(SnapshotCaptureFailure failure) =>
        Enqueue(() => _inner.RecordSnapshotCaptureSkipped(failure));

    public void RecordSnapshot(GameSnapshot snapshot) => Enqueue(() => _inner.RecordSnapshot(snapshot));

    public void RecordAdvisorRequest(AdvisorRequestDiagnostic request) =>
        Enqueue(() => _inner.RecordAdvisorRequest(request));

    public void RecordAdvisorAnalysis(AdvisorAnalysisDiagnostic analysis) =>
        Enqueue(() => _inner.RecordAdvisorAnalysis(analysis));

    public void RecordError(string code, Exception exception, string? stateId = null) =>
        Enqueue(() => _inner.RecordError(code, exception, stateId));

    public Task DrainAsync()
    {
        lock (_gate)
            return _tail;
    }

    private void Enqueue(Action action)
    {
        lock (_gate)
        {
            _tail = _tail.ContinueWith(
                _ => Execute(action),
                CancellationToken.None,
                TaskContinuationOptions.DenyChildAttach,
                TaskScheduler.Default);
        }
    }

    private static void Execute(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // Diagnostics must never interrupt HDT, including serialization failures.
        }
    }
}

public sealed class RedactedDiagnosticStore : IPluginDiagnostics
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Ignore,
        Formatting = Formatting.None
    };

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _logPath;
    private readonly long _maxBytes;
    private readonly int _retainedFiles;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SnapshotFixtureExporter? _fixtureExporter;
    private readonly string _sessionMode;
    private readonly Guid _runId;
    private readonly string _pluginVersion;
    private readonly string _ruleSetVersion;

    public RedactedDiagnosticStore(
        string directory,
        long maxBytes = 5 * 1024 * 1024,
        int retainedFiles = 3,
        Func<DateTimeOffset>? utcNow = null,
        SnapshotFixtureExporter? fixtureExporter = null,
        string sessionMode = "experimental",
        string pluginVersion = "unknown",
        string? ruleSetVersion = null,
        Guid? runId = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("A diagnostic directory is required.", nameof(directory));
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (retainedFiles < 1)
            throw new ArgumentOutOfRangeException(nameof(retainedFiles));
        if (string.IsNullOrWhiteSpace(sessionMode))
            throw new ArgumentException("A session mode is required.", nameof(sessionMode));
        if (string.IsNullOrWhiteSpace(pluginVersion))
            throw new ArgumentException("A plugin version is required.", nameof(pluginVersion));
        if (string.IsNullOrWhiteSpace(ruleSetVersion ?? TargetDeckProfile.RuleSetVersion))
            throw new ArgumentException("A rule-set version is required.", nameof(ruleSetVersion));

        _directory = directory;
        _logPath = Path.Combine(directory, "discard-advisor.jsonl");
        _maxBytes = maxBytes;
        _retainedFiles = retainedFiles;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _fixtureExporter = fixtureExporter;
        _sessionMode = sessionMode;
        _runId = runId ?? Guid.NewGuid();
        _pluginVersion = pluginVersion;
        _ruleSetVersion = ruleSetVersion ?? TargetDeckProfile.RuleSetVersion;
    }

    public void RecordGameStarted(Guid gameId) => Write(new DiagnosticEntry(
        _utcNow(),
        "game_started",
        null,
        new Dictionary<string, object>
        {
            ["gameId"] = gameId.ToString("N"),
            ["mode"] = _sessionMode,
            ["runId"] = _runId.ToString("N"),
            ["pluginVersion"] = _pluginVersion,
            ["ruleSetVersion"] = _ruleSetVersion
        }));

    public void RecordGameEnded(Guid gameId, bool completed) => Write(new DiagnosticEntry(
        _utcNow(),
        "game_ended",
        null,
        new Dictionary<string, object>
        {
            ["gameId"] = gameId.ToString("N"),
            ["mode"] = _sessionMode,
            ["runId"] = _runId.ToString("N"),
            ["pluginVersion"] = _pluginVersion,
            ["ruleSetVersion"] = _ruleSetVersion,
            ["completed"] = completed
        }));

    public void RecordGateDecision(GateDecision decision)
    {
        if (decision is null)
            throw new ArgumentNullException(nameof(decision));
        var data = new Dictionary<string, object> { ["status"] = decision.Status.ToString() };
        if (decision.ObservedCompatibility is { } compatibility)
        {
            data["hearthstoneBuild"] = compatibility.HearthstoneBuild;
            data["hdtVersion"] = compatibility.HdtVersion;
            data["cardDefsSha256"] = compatibility.CardDefsSha256;
            data["hearthDbSha256"] = compatibility.HearthDbSha256;
        }
        Write(new DiagnosticEntry(_utcNow(), "gate_decision", decision.ObservedDeckHash, data));
    }

    public void RecordSnapshotCaptureSkipped(SnapshotCaptureFailure failure)
    {
        if (failure == SnapshotCaptureFailure.None)
            throw new ArgumentOutOfRangeException(nameof(failure));
        Write(new DiagnosticEntry(
            _utcNow(),
            "snapshot_capture_skipped",
            null,
            new Dictionary<string, object> { ["reason"] = failure.ToString() }));
    }

    public void RecordSnapshot(GameSnapshot snapshot)
    {
        if (snapshot is null)
            throw new ArgumentNullException(nameof(snapshot));

        Write(new DiagnosticEntry(
            _utcNow(),
            "snapshot_ready",
            snapshot.StateId,
            new Dictionary<string, object>
            {
                ["turn"] = snapshot.TurnNumber,
                ["handCount"] = snapshot.Friendly.Hand.Count,
                ["friendlyBoardCount"] = snapshot.Friendly.Board.Count,
                ["opponentBoardCount"] = snapshot.Opponent.Board.Count,
                ["unsupportedInteractionCount"] = snapshot.Derived.UnsupportedInteractions.Count
            }));
        if (_fixtureExporter is null)
            return;
        try
        {
            _fixtureExporter.Export(snapshot);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void RecordAdvisorRequest(AdvisorRequestDiagnostic request)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        Write(new DiagnosticEntry(
            _utcNow(),
            "advisor_request",
            request.StateId,
            new Dictionary<string, object>
            {
                ["gameId"] = request.GameId.ToString("N"),
                ["mode"] = _sessionMode,
                ["runId"] = _runId.ToString("N"),
                ["pluginVersion"] = _pluginVersion,
                ["ruleSetVersion"] = _ruleSetVersion
            }));
    }

    public void RecordAdvisorAnalysis(AdvisorAnalysisDiagnostic analysis)
    {
        if (analysis is null)
            throw new ArgumentNullException(nameof(analysis));
        Write(new DiagnosticEntry(
            _utcNow(),
            "advisor_analysis",
            analysis.StateId,
            new Dictionary<string, object>
            {
                ["gameId"] = analysis.GameId.ToString("N"),
                ["mode"] = _sessionMode,
                ["runId"] = _runId.ToString("N"),
                ["pluginVersion"] = _pluginVersion,
                ["ruleSetVersion"] = _ruleSetVersion,
                ["disposition"] = analysis.Disposition.ToString(),
                ["status"] = analysis.Status.ToString(),
                ["elapsedMs"] = analysis.ElapsedMs,
                ["localSearchElapsedMs"] = analysis.LocalSearchElapsedMs,
                ["candidateCount"] = analysis.CandidateCount,
                ["unsupportedInteractionCount"] = analysis.UnsupportedInteractionCount,
                ["suggestionVisible"] = !string.Equals(_sessionMode, "shadow", StringComparison.OrdinalIgnoreCase) &&
                                          analysis.Disposition == AdvisorAnalysisDisposition.Published
            }));
    }

    public void RecordError(string code, Exception exception, string? stateId = null)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("An error code is required.", nameof(code));
        if (exception is null)
            throw new ArgumentNullException(nameof(exception));

        var data = new Dictionary<string, object>
        {
            ["code"] = code,
            ["exceptionType"] = exception.GetType().Name
        };
        if (exception is FileNotFoundException fileNotFound && !string.IsNullOrWhiteSpace(fileNotFound.FileName))
            data["missingFile"] = RedactFileName(fileNotFound.FileName!);

        Write(new DiagnosticEntry(
            _utcNow(),
            "error",
            stateId,
            data));
    }

    private static string RedactFileName(string value)
    {
        try
        {
            var separator = value.LastIndexOfAny(new[] { '/', '\\' });
            return separator >= 0 ? value.Substring(separator + 1) : value;
        }
        catch (ArgumentException)
        {
            return "invalid-file-name";
        }
    }

    private void Write(DiagnosticEntry entry)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                RotateIfNeeded();
                var line = JsonConvert.SerializeObject(entry, JsonSettings) + Environment.NewLine;
                File.AppendAllText(_logPath, line, new UTF8Encoding(false));
            }
        }
        catch (IOException)
        {
            // Diagnostics must never interrupt HDT or snapshot processing.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_logPath) || new FileInfo(_logPath).Length < _maxBytes)
            return;

        var oldest = RotatedPath(_retainedFiles);
        if (File.Exists(oldest))
            File.Delete(oldest);
        for (var index = _retainedFiles - 1; index >= 1; index--)
        {
            var source = RotatedPath(index);
            if (File.Exists(source))
                File.Move(source, RotatedPath(index + 1));
        }
        File.Move(_logPath, RotatedPath(1));
    }

    private string RotatedPath(int index) => _logPath + "." + index.ToString(CultureInfo.InvariantCulture);

    private sealed class DiagnosticEntry
    {
        public DiagnosticEntry(DateTimeOffset timestamp, string eventName, string? stateId, IReadOnlyDictionary<string, object> data)
        {
            Timestamp = timestamp;
            Event = eventName;
            StateId = stateId;
            Data = data;
        }

        public DateTimeOffset Timestamp { get; }
        public string Event { get; }
        public string? StateId { get; }
        public IReadOnlyDictionary<string, object> Data { get; }
    }
}

public sealed class SnapshotFixtureExporter
{
    private readonly object _gate = new();
    private readonly string _directory;

    public SnapshotFixtureExporter(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("A fixture directory is required.", nameof(directory));
        _directory = directory;
    }

    public string Export(GameSnapshot snapshot)
    {
        if (snapshot is null)
            throw new ArgumentNullException(nameof(snapshot));

        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            var safeStateId = string.Concat(snapshot.StateId.Select(character =>
                char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_'));
            var path = Path.Combine(_directory, safeStateId + ".snapshot.json");
            var temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, SnapshotJsonSerializer.Serialize(snapshot), new UTF8Encoding(false));
            if (File.Exists(path))
                File.Delete(path);
            File.Move(temporaryPath, path);
            return path;
        }
    }
}
