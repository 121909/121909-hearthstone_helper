using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace DiscardAdvisor.Replay;

public sealed record RegressionReportPaths(string JsonPath, string MarkdownPath, string ExpertReviewPath);

public sealed class RegressionReportWriter
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Include
    };

    public RegressionReportPaths Write(OfflineRegressionReport report, string outputDirectory)
    {
        if (report is null)
            throw new ArgumentNullException(nameof(report));
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("An output directory is required.", nameof(outputDirectory));
        var directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        var jsonPath = Path.Combine(directory, "offline-regression.json");
        var markdownPath = Path.Combine(directory, "offline-regression.md");
        var expertReviewPath = Path.Combine(directory, "expert-review-pack.json");
        File.WriteAllText(jsonPath, JsonConvert.SerializeObject(report, JsonSettings) + Environment.NewLine, new UTF8Encoding(false));
        File.WriteAllText(markdownPath, BuildMarkdown(report), new UTF8Encoding(false));
        File.WriteAllText(
            expertReviewPath,
            JsonConvert.SerializeObject(BuildExpertReviewPack(report), JsonSettings) + Environment.NewLine,
            new UTF8Encoding(false));
        return new RegressionReportPaths(jsonPath, markdownPath, expertReviewPath);
    }

    private static string BuildMarkdown(OfflineRegressionReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Offline regression report");
        builder.AppendLine();
        builder.AppendLine($"- Generated (UTC): `{report.GeneratedAtUtc:O}`");
        builder.AppendLine($"- Result: **{(report.Passed ? "PASS" : "FAIL")}**");
        builder.AppendLine($"- Replays: {report.ReplayCount}");
        builder.AppendLine($"- Snapshots: {report.EvaluatedSnapshotCount}/{report.SnapshotCount} evaluated");
        builder.AppendLine($"- Legal routes: {report.LegalRouteCount}/{report.RouteCount} ({FormatPercent(report.LegalRouteRate)})");
        builder.AppendLine($"- Latency p50/p95/max: {report.LatencyP50Ms:F2}/{report.LatencyP95Ms:F2}/{report.LatencyMaximumMs:F2} ms");
        builder.AppendLine($"- Deadline expiration: {report.DeadlineExpiredCount}/{report.EvaluatedSnapshotCount} ({FormatPercent(report.DeadlineExpirationRate)})");
        builder.AppendLine($"- Expert annotations: {report.AnnotatedSnapshotCount}/200");
        builder.AppendLine($"- Expert Top-3 consistency: {FormatOptionalPercent(report.ExpertTop3ConsistencyRate)} ({report.ExpertTop3MatchCount}/{report.AnnotatedSnapshotCount}, target 80%)");
        builder.AppendLine($"- Expert thresholds: **{(report.MeetsExpertAnnotationTarget ? "MET" : "NOT MET")}**");
        builder.AppendLine();
        builder.AppendLine("## Shadow run");
        builder.AppendLine();
        if (report.ShadowRun.LogFileCount == 0)
        {
            builder.AppendLine("No shadow-run telemetry supplied.");
        }
        else
        {
            builder.AppendLine($"- Completed games: {report.ShadowRun.CompletedGameCount}/50 ({report.ShadowRun.StartedGameCount} started)");
            builder.AppendLine($"- Automated acceptance thresholds: **{(report.ShadowRun.MeetsAutomatedAcceptanceThresholds ? "MET" : "NOT MET")}**");
            builder.AppendLine($"- Analyses: {report.ShadowRun.AnalysisCount}");
            builder.AppendLine($"- Published/superseded/cancelled/failed: {report.ShadowRun.PublishedCount}/{report.ShadowRun.SupersededCount}/{report.ShadowRun.CancelledCount}/{report.ShadowRun.FailedCount}");
            builder.AppendLine($"- Superseded rate: {FormatPercent(report.ShadowRun.SupersededRate)}");
            builder.AppendLine($"- Latency p50/p95/max: {report.ShadowRun.LatencyP50Ms:F2}/{report.ShadowRun.LatencyP95Ms:F2}/{report.ShadowRun.LatencyMaximumMs:F2} ms");
            builder.AppendLine($"- Duplicate state requests: {report.ShadowRun.DuplicateRequestCount}");
            builder.AppendLine($"- Visible suggestions: {report.ShadowRun.VisibleSuggestionCount}");
        }
        builder.AppendLine();
        builder.AppendLine("## Unsupported interactions");
        builder.AppendLine();
        if (report.UnsupportedInteractions.IsEmpty)
            builder.AppendLine("None.");
        else
        {
            builder.AppendLine("| Interaction | Snapshots |");
            builder.AppendLine("| --- | ---: |");
            foreach (var item in report.UnsupportedInteractions.OrderBy(item => item.Key, StringComparer.Ordinal))
                builder.AppendLine($"| `{EscapeCell(item.Key)}` | {item.Value} |");
        }
        builder.AppendLine();
        builder.AppendLine("## Input errors");
        builder.AppendLine();
        if (report.InputErrors.IsEmpty)
            builder.AppendLine("None.");
        else
        {
            foreach (var error in report.InputErrors)
                builder.AppendLine($"- {error}");
        }
        builder.AppendLine();
        builder.AppendLine("Deadline expiration is an offline deadline check. Actual state supersession is reported separately when shadow-run telemetry is supplied.");
        return builder.ToString();
    }

    private static object BuildExpertReviewPack(OfflineRegressionReport report) => new
    {
        ProtocolVersion = "1.0.0",
        TargetAnnotationCount = 200,
        AnnotatedSnapshotCount = report.AnnotatedSnapshotCount,
        RemainingToTarget = Math.Max(0, 200 - report.AnnotatedSnapshotCount),
        Pending = report.Snapshots
            .Where(snapshot => snapshot.MappingSupported && !snapshot.ExpertTop3Matched.HasValue)
            .Select(snapshot => new
            {
                snapshot.StateId,
                snapshot.Source,
                snapshot.TurnNumber,
                Candidates = snapshot.ReviewCandidates,
                AnnotationTemplate = new
                {
                    ProtocolVersion = "1.0.0",
                    snapshot.StateId,
                    ExpertTop3 = Array.Empty<object>()
                }
            })
            .ToArray()
    };

    private static string FormatPercent(double value) => value.ToString("P2", CultureInfo.InvariantCulture);

    private static string FormatOptionalPercent(double? value) => value.HasValue ? FormatPercent(value.Value) : "N/A";

    private static string EscapeCell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}
