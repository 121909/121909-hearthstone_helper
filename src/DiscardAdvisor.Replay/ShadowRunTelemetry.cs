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
    bool Completed,
    string? RunId = null,
    string? PluginVersion = null,
    string? RuleSetVersion = null);

public sealed record ShadowVersionCohort(
    string PluginVersion,
    string RuleSetVersion,
    int StartedGameCount,
    int CompletedGameCount,
    int RequestCount,
    int AnalysisCount);

public sealed record ShadowAdvisorRequestObservation(
    DateTimeOffset Timestamp,
    long Sequence,
    Guid GameId,
    string StateId,
    string Mode);

public sealed record ShadowAnalysisObservation(
    DateTimeOffset Timestamp,
    long Sequence,
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
    ImmutableArray<ShadowAdvisorRequestObservation> Requests,
    ImmutableArray<ShadowAnalysisObservation> Analyses,
    ImmutableArray<string> Errors)
{
    public static ShadowRunTelemetry Empty { get; } = new(
        0,
        ImmutableArray<ShadowGameSession>.Empty,
        ImmutableArray<ShadowAdvisorRequestObservation>.Empty,
        ImmutableArray<ShadowAnalysisObservation>.Empty,
        ImmutableArray<string>.Empty);
}

public sealed record ShadowRunReport(
    int LogFileCount,
    int StartedGameCount,
    int CompletedGameCount,
    int CompletedGameWithPublishedAnalysisCount,
    int RequestCount,
    int AnalysisCount,
    int PublishedCount,
    int SupersededCount,
    int CancelledCount,
    int FailedCount,
    int DuplicateRequestCount,
    int MissingRequestCount,
    int UnfinishedRequestCount,
    int VisibleSuggestionCount,
    int UnsupportedAnalysisCount,
    int UnsupportedInteractionOccurrenceCount,
    int RunCount,
    int VersionCohortCount,
    int MissingVersionMetadataGameCount,
    ImmutableArray<ShadowVersionCohort> VersionCohorts,
    double LatencyP50Ms,
    double LatencyP95Ms,
    double LatencyMaximumMs)
{
    public double SupersededRate => AnalysisCount == 0 ? 0 : (double)SupersededCount / AnalysisCount;

    public int CompletedGameWithoutPublishedAnalysisCount =>
        Math.Max(0, CompletedGameCount - CompletedGameWithPublishedAnalysisCount);

    public bool MeetsAutomatedAcceptanceThresholds => CompletedGameWithPublishedAnalysisCount >= 50 &&
                                                       AnalysisCount > 0 &&
                                                       RequestCount == AnalysisCount &&
                                                       MissingRequestCount == 0 &&
                                                       UnfinishedRequestCount == 0 &&
                                                       MissingVersionMetadataGameCount == 0 &&
                                                       VersionCohortCount == 1 &&
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
        var requests = telemetry.Requests.Where(request =>
            IsShadow(request.Mode) || gameIds.Contains(request.GameId)).ToArray();
        var analyses = telemetry.Analyses.Where(analysis =>
            IsShadow(analysis.Mode) || gameIds.Contains(analysis.GameId)).ToArray();
        var latencies = analyses.Select(analysis => analysis.ElapsedMs).OrderBy(value => value).ToArray();
        var duplicates = CountDuplicateRequests(requests, analyses);
        var missingRequests = CountRequestImbalance(requests, analyses, missingRequests: true);
        var unfinishedRequests = CountRequestImbalance(requests, analyses, missingRequests: false);
        var versionCohorts = games
            .Where(HasVersionMetadata)
            .GroupBy(game => (game.PluginVersion!, game.RuleSetVersion!))
            .OrderBy(group => group.Key.Item1, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Item2, StringComparer.Ordinal)
            .Select(group =>
            {
                var cohortGameIds = group.Select(game => game.GameId).ToHashSet();
                return new ShadowVersionCohort(
                    group.Key.Item1,
                    group.Key.Item2,
                    group.Count(game => game.Started),
                    group.Count(game => game.Started && game.Ended && game.Completed),
                    requests.Count(request => cohortGameIds.Contains(request.GameId)),
                    analyses.Count(analysis => cohortGameIds.Contains(analysis.GameId)));
            })
            .ToImmutableArray();
        var publishedGameIds = analyses
            .Where(analysis => string.Equals(analysis.Disposition, "Published", StringComparison.OrdinalIgnoreCase))
            .Select(analysis => analysis.GameId)
            .ToHashSet();
        return new ShadowRunReport(
            telemetry.LogFileCount,
            games.Count(game => game.Started),
            games.Count(game => game.Started && game.Ended && game.Completed),
            games.Count(game => game.Started && game.Ended && game.Completed && publishedGameIds.Contains(game.GameId)),
            requests.Length,
            analyses.Length,
            CountDisposition(analyses, "Published"),
            CountDisposition(analyses, "Superseded"),
            CountDisposition(analyses, "Cancelled"),
            CountDisposition(analyses, "Failed"),
            duplicates,
            missingRequests,
            unfinishedRequests,
            analyses.Count(analysis => analysis.SuggestionVisible),
            analyses.Count(analysis =>
                string.Equals(analysis.Status, "UnsupportedInteraction", StringComparison.OrdinalIgnoreCase) ||
                analysis.UnsupportedInteractionCount > 0),
            analyses.Sum(analysis => analysis.UnsupportedInteractionCount),
            games.Where(game => !string.IsNullOrWhiteSpace(game.RunId))
                .Select(game => game.RunId)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            versionCohorts.Length,
            games.Count(game => game.Started && !HasVersionMetadata(game)),
            versionCohorts,
            Percentile(latencies, 0.50),
            Percentile(latencies, 0.95),
            latencies.Length == 0 ? 0 : latencies[^1]);
    }

    private static bool IsShadow(string value) => string.Equals(value, "shadow", StringComparison.OrdinalIgnoreCase);

    private static bool HasVersionMetadata(ShadowGameSession game) =>
        !string.IsNullOrWhiteSpace(game.RunId) &&
        !string.IsNullOrWhiteSpace(game.PluginVersion) &&
        !string.IsNullOrWhiteSpace(game.RuleSetVersion);

    private static int CountDisposition(IEnumerable<ShadowAnalysisObservation> analyses, string disposition) =>
        analyses.Count(analysis => string.Equals(analysis.Disposition, disposition, StringComparison.OrdinalIgnoreCase));

    private static int CountDuplicateRequests(
        IEnumerable<ShadowAdvisorRequestObservation> requests,
        IEnumerable<ShadowAnalysisObservation> analyses)
    {
        var requestArray = requests.ToArray();
        var analysisArray = analyses.ToArray();
        var keys = requestArray.Select(request => (request.GameId, request.StateId))
            .Concat(analysisArray.Select(analysis => (analysis.GameId, analysis.StateId)))
            .Distinct(new GameStateComparer());
        var duplicateCount = 0;
        foreach (var key in keys)
        {
            var timeline = requestArray.Where(request => SameState(request.GameId, request.StateId, key))
                .Select(request => new TimelineEvent(request.Timestamp, request.Sequence, true, null))
                .Concat(analysisArray.Where(analysis => SameState(analysis.GameId, analysis.StateId, key))
                    .Select(analysis => new TimelineEvent(
                        analysis.Timestamp,
                        analysis.Sequence,
                        false,
                        analysis.Disposition)))
                .OrderBy(item => item.Timestamp)
                .ThenBy(item => item.Sequence)
                .ToArray();
            var outstanding = 0;
            var published = false;
            foreach (var item in timeline)
            {
                if (item.IsRequest)
                {
                    if (outstanding > 0 || published)
                        duplicateCount++;
                    outstanding++;
                    continue;
                }
                if (outstanding > 0)
                    outstanding--;
                if (string.Equals(item.Disposition, "Published", StringComparison.OrdinalIgnoreCase))
                    published = true;
            }
        }
        return duplicateCount;
    }

    private static int CountRequestImbalance(
        IEnumerable<ShadowAdvisorRequestObservation> requests,
        IEnumerable<ShadowAnalysisObservation> analyses,
        bool missingRequests)
    {
        var requestCounts = requests.GroupBy(request => (request.GameId, request.StateId), new GameStateComparer())
            .ToDictionary(group => group.Key, group => group.Count(), new GameStateComparer());
        var analysisCounts = analyses.GroupBy(analysis => (analysis.GameId, analysis.StateId), new GameStateComparer())
            .ToDictionary(group => group.Key, group => group.Count(), new GameStateComparer());
        var keys = requestCounts.Keys.Concat(analysisCounts.Keys).Distinct(new GameStateComparer());
        return keys.Sum(key =>
        {
            var requestCount = requestCounts.TryGetValue(key, out var observedRequests) ? observedRequests : 0;
            var analysisCount = analysisCounts.TryGetValue(key, out var observedAnalyses) ? observedAnalyses : 0;
            return missingRequests
                ? Math.Max(0, analysisCount - requestCount)
                : Math.Max(0, requestCount - analysisCount);
        });
    }

    private static bool SameState(Guid gameId, string stateId, (Guid GameId, string StateId) expected) =>
        gameId == expected.GameId && string.Equals(stateId, expected.StateId, StringComparison.Ordinal);

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

    private sealed record TimelineEvent(
        DateTimeOffset Timestamp,
        long Sequence,
        bool IsRequest,
        string? Disposition);
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
        var requests = ImmutableArray.CreateBuilder<ShadowAdvisorRequestObservation>();
        var analyses = ImmutableArray.CreateBuilder<ShadowAnalysisObservation>();
        var errors = ImmutableArray.CreateBuilder<string>();
        long sequence = 0;
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
                ReadFile(path, games, requests, analyses, errors, ref sequence);
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
            requests.ToImmutable(),
            analyses.ToImmutable(),
            errors.ToImmutable());
    }

    private static void ReadFile(
        string path,
        IDictionary<Guid, MutableGameSession> games,
        ImmutableArray<ShadowAdvisorRequestObservation>.Builder requests,
        ImmutableArray<ShadowAnalysisObservation>.Builder analyses,
        ImmutableArray<string>.Builder errors,
        ref long sequence)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, false);
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            sequence++;
            try
            {
                ReadEntry(JObject.Parse(line), games, requests, analyses, sequence);
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
        ImmutableArray<ShadowAdvisorRequestObservation>.Builder requests,
        ImmutableArray<ShadowAnalysisObservation>.Builder analyses,
        long sequence)
    {
        var eventName = RequiredString(entry, "event");
        if (eventName is not ("game_started" or "game_ended" or "advisor_request" or "advisor_analysis"))
            return;
        var data = entry["data"] as JObject ?? throw new InvalidOperationException("Telemetry entry requires a data object.");
        var gameId = Guid.ParseExact(RequiredString(data, "gameId"), "N");
        var mode = RequiredString(data, "mode");
        var runId = OptionalGuid(data, "runId");
        var pluginVersion = OptionalString(data, "pluginVersion");
        var ruleSetVersion = OptionalString(data, "ruleSetVersion");
        if (!games.TryGetValue(gameId, out var game))
        {
            game = new MutableGameSession(gameId, mode, runId, pluginVersion, ruleSetVersion);
            games.Add(gameId, game);
        }
        else
        {
            game.MergeMetadata(mode, runId, pluginVersion, ruleSetVersion);
        }
        if (eventName is "game_started" or "game_ended")
        {
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
        var timestamp = RequiredTimestamp(entry, "timestamp");
        if (eventName == "advisor_request")
        {
            requests.Add(new ShadowAdvisorRequestObservation(timestamp, sequence, gameId, stateId, mode));
            return;
        }
        analyses.Add(new ShadowAnalysisObservation(
            timestamp,
            sequence,
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

    private static DateTimeOffset RequiredTimestamp(JObject value, string name)
    {
        var text = RequiredString(value, name);
        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var timestamp)
            ? timestamp
            : throw new InvalidOperationException($"Telemetry field '{name}' must be an ISO-8601 timestamp.");
    }

    private static string RequiredString(JObject value, string name) =>
        value.Value<string>(name) is { Length: > 0 } result
            ? result
            : throw new InvalidOperationException($"Telemetry field '{name}' is required.");

    private static string? OptionalString(JObject value, string name) =>
        value.Value<string>(name) is { Length: > 0 } result ? result : null;

    private static string? OptionalGuid(JObject value, string name)
    {
        var result = OptionalString(value, name);
        if (result is null)
            return null;
        return Guid.TryParseExact(result, "N", out var guid)
            ? guid.ToString("N")
            : throw new InvalidOperationException($"Telemetry field '{name}' must be a 32-character GUID.");
    }

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
        public MutableGameSession(
            Guid gameId,
            string mode,
            string? runId,
            string? pluginVersion,
            string? ruleSetVersion)
        {
            GameId = gameId;
            Mode = mode;
            RunId = runId;
            PluginVersion = pluginVersion;
            RuleSetVersion = ruleSetVersion;
        }

        public Guid GameId { get; }
        public string Mode { get; private set; }
        public string? RunId { get; private set; }
        public string? PluginVersion { get; private set; }
        public string? RuleSetVersion { get; private set; }
        public bool Started { get; set; }
        public bool Ended { get; set; }
        public bool Completed { get; set; }

        public void MergeMetadata(
            string mode,
            string? runId,
            string? pluginVersion,
            string? ruleSetVersion)
        {
            Mode = Merge("mode", Mode, mode)!;
            RunId = Merge("runId", RunId, runId);
            PluginVersion = Merge("pluginVersion", PluginVersion, pluginVersion);
            RuleSetVersion = Merge("ruleSetVersion", RuleSetVersion, ruleSetVersion);
        }

        public ShadowGameSession ToImmutable() => new(
            GameId,
            Mode,
            Started,
            Ended,
            Completed,
            RunId,
            PluginVersion,
            RuleSetVersion);

        private static string? Merge(string name, string? current, string? observed)
        {
            if (string.IsNullOrWhiteSpace(observed))
                return current;
            if (!string.IsNullOrWhiteSpace(current) && !string.Equals(current, observed, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Telemetry field '{name}' changed within one game.");
            return observed;
        }
    }
}
