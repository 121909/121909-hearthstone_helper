using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DiscardAdvisor.Replay;

public sealed record ShadowGameSession(
    Guid GameId,
    string Mode,
    bool Started,
    bool Ended,
    bool Completed);

public sealed record ShadowAnalysisObservation(
    Guid GameId,
    string StateId,
    string Mode,
    string Disposition,
    string Status,
    double ElapsedMs,
    double LocalSearchElapsedMs,
    int CandidateCount,
    int UnsupportedInteractionCount,
    bool SuggestionVisible);

public sealed record ShadowRunTelemetry(
    int LogFileCount,
    ImmutableArray<ShadowGameSession> Games,
    ImmutableArray<ShadowAnalysisObservation> Analyses,
    ImmutableArray<string> Errors)
{
    public static ShadowRunTelemetry Empty { get; } = new(
        0,
        ImmutableArray<ShadowGameSession>.Empty,
        ImmutableArray<ShadowAnalysisObservation>.Empty,
        ImmutableArray<string>.Empty);
}

public sealed record ShadowRunReport(
    int LogFileCount,
    int StartedGameCount,
    int CompletedGameCount,
    int AnalysisCount,
    int PublishedCount,
    int SupersededCount,
    int CancelledCount,
    int FailedCount,
    int DuplicateRequestCount,
    int VisibleSuggestionCount,
    double LatencyP50Ms,
    double LatencyP95Ms,
    double LatencyMaximumMs)
{
    public double SupersededRate => AnalysisCount == 0 ? 0 : (double)SupersededCount / AnalysisCount;

    public bool MeetsAutomatedAcceptanceThresholds => CompletedGameCount >= 50 &&
                                                       AnalysisCount > 0 &&
                                                       FailedCount == 0 &&
                                                       DuplicateRequestCount == 0 &&
                                                       VisibleSuggestionCount == 0 &&
                                                       LatencyP95Ms < 300;

    public static ShadowRunReport FromTelemetry(ShadowRunTelemetry telemetry)
    {
        if (telemetry is null)
            throw new ArgumentNullException(nameof(telemetry));
        var games = telemetry.Games.Where(game => IsShadow(game.Mode)).ToArray();
        var gameIds = games.Select(game => game.GameId).ToHashSet();
        var analyses = telemetry.Analyses.Where(analysis =>
            IsShadow(analysis.Mode) || gameIds.Contains(analysis.GameId)).ToArray();
        var latencies = analyses.Select(analysis => analysis.ElapsedMs).OrderBy(value => value).ToArray();
        var duplicates = analyses.GroupBy(
                analysis => (analysis.GameId, analysis.StateId),
                new GameStateComparer())
            .Sum(group => Math.Max(0, group.Count() - 1));
        return new ShadowRunReport(
            telemetry.LogFileCount,
            games.Count(game => game.Started),
            games.Count(game => game.Started && game.Ended && game.Completed),
            analyses.Length,
            CountDisposition(analyses, "Published"),
            CountDisposition(analyses, "Superseded"),
            CountDisposition(analyses, "Cancelled"),
            CountDisposition(analyses, "Failed"),
            duplicates,
            analyses.Count(analysis => analysis.SuggestionVisible),
            Percentile(latencies, 0.50),
            Percentile(latencies, 0.95),
            latencies.Length == 0 ? 0 : latencies[^1]);
    }

    private static bool IsShadow(string value) => string.Equals(value, "shadow", StringComparison.OrdinalIgnoreCase);

    private static int CountDisposition(IEnumerable<ShadowAnalysisObservation> analyses, string disposition) =>
        analyses.Count(analysis => string.Equals(analysis.Disposition, disposition, StringComparison.OrdinalIgnoreCase));

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
            return 0;
        var rank = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Max(0, Math.Min(rank, sorted.Count - 1))];
    }

    private sealed class GameStateComparer : IEqualityComparer<(Guid GameId, string StateId)>
    {
        public bool Equals((Guid GameId, string StateId) x, (Guid GameId, string StateId) y) =>
            x.GameId == y.GameId && string.Equals(x.StateId, y.StateId, StringComparison.Ordinal);

        public int GetHashCode((Guid GameId, string StateId) value) =>
            (value.GameId.GetHashCode() * 397) ^ StringComparer.Ordinal.GetHashCode(value.StateId);
    }
}

public sealed class ShadowRunTelemetryReader
{
    public const long MaximumLogBytes = 32L * 1024 * 1024;

    public ShadowRunTelemetry Read(IEnumerable<string> paths)
    {
        if (paths is null)
            throw new ArgumentNullException(nameof(paths));
        var files = paths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var games = new Dictionary<Guid, MutableGameSession>();
        var analyses = ImmutableArray.CreateBuilder<ShadowAnalysisObservation>();
        var errors = ImmutableArray.CreateBuilder<string>();
        foreach (var path in files)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    errors.Add($"{path}: diagnostic log does not exist.");
                    continue;
                }
                if (info.Length > MaximumLogBytes)
                {
                    errors.Add($"{path}: diagnostic log exceeds 32 MiB.");
                    continue;
                }
                ReadFile(path, games, analyses, errors);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{path}: {exception.Message}");
            }
        }

        return new ShadowRunTelemetry(
            files.Length,
            games.Values.Select(game => game.ToImmutable())
                .OrderBy(game => game.GameId)
                .ToImmutableArray(),
            analyses.ToImmutable(),
            errors.ToImmutable());
    }

    private static void ReadFile(
        string path,
        IDictionary<Guid, MutableGameSession> games,
        ImmutableArray<ShadowAnalysisObservation>.Builder analyses,
        ImmutableArray<string>.Builder errors)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, false);
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                ReadEntry(JObject.Parse(line), games, analyses);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
            {
                errors.Add($"{path}:{lineNumber}: {exception.Message}");
            }
        }
    }

    private static void ReadEntry(
        JObject entry,
        IDictionary<Guid, MutableGameSession> games,
        ImmutableArray<ShadowAnalysisObservation>.Builder analyses)
    {
        var eventName = RequiredString(entry, "event");
        if (eventName is not ("game_started" or "game_ended" or "advisor_analysis"))
            return;
        var data = entry["data"] as JObject ?? throw new InvalidOperationException("Telemetry entry requires a data object.");
        var gameId = Guid.ParseExact(RequiredString(data, "gameId"), "N");
        var mode = RequiredString(data, "mode");
        if (eventName is "game_started" or "game_ended")
        {
            if (!games.TryGetValue(gameId, out var game))
            {
                game = new MutableGameSession(gameId, mode);
                games.Add(gameId, game);
            }
            game.Mode = mode;
            game.Started |= eventName == "game_started";
            game.Ended |= eventName == "game_ended";
            if (eventName == "game_ended")
            {
                game.Completed |= data.Value<bool?>("completed") ??
                                  throw new InvalidOperationException("Telemetry field 'completed' is required.");
            }
            return;
        }

        var stateId = RequiredString(entry, "stateId");
        analyses.Add(new ShadowAnalysisObservation(
            gameId,
            stateId,
            mode,
            RequiredString(data, "disposition"),
            RequiredString(data, "status"),
            RequiredDouble(data, "elapsedMs"),
            RequiredDouble(data, "localSearchElapsedMs"),
            RequiredInt(data, "candidateCount"),
            RequiredInt(data, "unsupportedInteractionCount"),
            data.Value<bool?>("suggestionVisible") ?? false));
    }

    private static string RequiredString(JObject value, string name) =>
        value.Value<string>(name) is { Length: > 0 } result
            ? result
            : throw new InvalidOperationException($"Telemetry field '{name}' is required.");

    private static double RequiredDouble(JObject value, string name)
    {
        var result = value.Value<double?>(name) ??
                     throw new InvalidOperationException($"Telemetry field '{name}' is required.");
        if (result < 0 || double.IsNaN(result) || double.IsInfinity(result))
            throw new InvalidOperationException($"Telemetry field '{name}' must be a non-negative finite number.");
        return result;
    }

    private static int RequiredInt(JObject value, string name)
    {
        var result = value.Value<int?>(name) ??
                     throw new InvalidOperationException($"Telemetry field '{name}' is required.");
        if (result < 0)
            throw new InvalidOperationException($"Telemetry field '{name}' must be non-negative.");
        return result;
    }

    private sealed class MutableGameSession
    {
        public MutableGameSession(Guid gameId, string mode)
        {
            GameId = gameId;
            Mode = mode;
        }

        public Guid GameId { get; }
        public string Mode { get; set; }
        public bool Started { get; set; }
        public bool Ended { get; set; }
        public bool Completed { get; set; }

        public ShadowGameSession ToImmutable() => new(GameId, Mode, Started, Ended, Completed);
    }
}
