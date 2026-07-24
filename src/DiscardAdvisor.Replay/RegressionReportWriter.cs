using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
        if (report.IgnoredRuleSetSnapshotCount > 0)
            builder.AppendLine($"- Historical snapshots ignored from other rule sets: {report.IgnoredRuleSetSnapshotCount}");
        builder.AppendLine($"- Legal routes: {report.LegalRouteCount}/{report.RouteCount} ({FormatPercent(report.LegalRouteRate)})");
        builder.AppendLine($"- Latency p50/p95/max: {report.LatencyP50Ms:F2}/{report.LatencyP95Ms:F2}/{report.LatencyMaximumMs:F2} ms");
        builder.AppendLine($"- Deadline expiration: {report.DeadlineExpiredCount}/{report.EvaluatedSnapshotCount} ({FormatPercent(report.DeadlineExpirationRate)})");
        builder.AppendLine($"- Expert annotation gate: **{(report.ExpertAnnotationsRequired ? "REQUIRED" : "DISABLED")}**");
        if (report.AnnotatedSnapshotCount > 0)
        {
            builder.AppendLine($"- Optional expert annotations: {report.QualifiedAnnotatedSnapshotCount} qualified ({report.AnnotatedSnapshotCount} total, {report.UnqualifiedAnnotatedSnapshotCount} legacy or unqualified)");
            builder.AppendLine($"- Optional expert primary route in Advisor Top-3: {FormatOptionalPercent(report.QualifiedExpertTop3ConsistencyRate)} ({report.QualifiedExpertTop3MatchCount}/{report.QualifiedAnnotatedSnapshotCount})");
        }
        builder.AppendLine($"- Visible-suggestion prerequisites: **{(report.MeetsVisibleSuggestionPrerequisites ? "MET" : "NOT MET")}**");
        builder.AppendLine();
        builder.AppendLine("## Shadow run");
        builder.AppendLine();
        if (report.ShadowRun.LogFileCount == 0)
        {
            builder.AppendLine("No shadow-run telemetry supplied.");
        }
        else
        {
            builder.AppendLine($"- Completed games with a published Shadow analysis: {report.ShadowRun.CompletedGameWithPublishedAnalysisCount}/{ValidationPolicy.RequiredShadowGameCount} ({report.ShadowRun.CompletedGameCount} completed, {report.ShadowRun.StartedGameCount} started)");
            builder.AppendLine($"- Completed games without a published analysis: {report.ShadowRun.CompletedGameWithoutPublishedAnalysisCount}");
            builder.AppendLine($"- Runs / version cohorts / games missing metadata: {report.ShadowRun.RunCount}/{report.ShadowRun.VersionCohortCount}/{report.ShadowRun.MissingVersionMetadataGameCount}");
            if (report.ShadowRun.TargetPluginVersion is not null)
            {
                builder.AppendLine($"- Target cohort: `{EscapeCell(report.ShadowRun.TargetPluginVersion)}` / `{EscapeCell(report.ShadowRun.TargetRuleSetVersion ?? string.Empty)}`");
                builder.AppendLine($"- Historical games ignored from other cohorts: {report.ShadowRun.IgnoredVersionGameCount}");
            }
            builder.AppendLine($"- Automated acceptance thresholds: **{(report.ShadowRun.MeetsAutomatedAcceptanceThresholds ? "MET" : "NOT MET")}**");
            builder.AppendLine($"- Requests/terminal analyses: {report.ShadowRun.RequestCount}/{report.ShadowRun.AnalysisCount}");
            builder.AppendLine($"- Published/superseded/cancelled/failed: {report.ShadowRun.PublishedCount}/{report.ShadowRun.SupersededCount}/{report.ShadowRun.CancelledCount}/{report.ShadowRun.FailedCount}");
            builder.AppendLine($"- Superseded rate: {FormatPercent(report.ShadowRun.SupersededRate)}");
            builder.AppendLine($"- Latency p50/p95/max: {report.ShadowRun.LatencyP50Ms:F2}/{report.ShadowRun.LatencyP95Ms:F2}/{report.ShadowRun.LatencyMaximumMs:F2} ms");
            builder.AppendLine($"- Duplicate state requests: {report.ShadowRun.DuplicateRequestCount}");
            builder.AppendLine($"- Missing request starts / unfinished requests: {report.ShadowRun.MissingRequestCount}/{report.ShadowRun.UnfinishedRequestCount}");
            builder.AppendLine($"- Visible suggestions: {report.ShadowRun.VisibleSuggestionCount}");
            builder.AppendLine($"- Unsupported analyses / occurrences: {report.ShadowRun.UnsupportedAnalysisCount}/{report.ShadowRun.UnsupportedInteractionOccurrenceCount}");
            if (!report.ShadowRun.VersionCohorts.IsEmpty)
            {
                builder.AppendLine();
                builder.AppendLine("| Plugin | Rules | Started | Completed | Requests | Analyses |");
                builder.AppendLine("| --- | --- | ---: | ---: | ---: | ---: |");
                foreach (var cohort in report.ShadowRun.VersionCohorts)
                {
                    builder.AppendLine($"| `{EscapeCell(cohort.PluginVersion)}` | `{EscapeCell(cohort.RuleSetVersion)}` | {cohort.StartedGameCount} | {cohort.CompletedGameCount} | {cohort.RequestCount} | {cohort.AnalysisCount} |");
                }
            }
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
        ReviewMethod = "BLIND_ROUTE_RANKING",
        Instructions = "Expert annotations are optional and do not affect the active release gate. When supplied, rank routes without opening offline-regression.json and put the strongest route first in expertTop3.",
        TargetAnnotationCount = 0,
        QualifiedAnnotatedSnapshotCount = report.QualifiedAnnotatedSnapshotCount,
        LegacyOrUnqualifiedAnnotationCount = report.UnqualifiedAnnotatedSnapshotCount,
        RemainingToTarget = 0,
        Pending = report.Snapshots
            .Where(snapshot => snapshot.MappingSupported && !snapshot.AnnotationQualified)
            .Select(snapshot => new
            {
                snapshot.StateId,
                snapshot.Source,
                snapshot.TurnNumber,
                Options = BlindOrder(snapshot)
                    .Select((candidate, index) => new
                    {
                        ReviewOptionId = $"option-{index + 1}",
                        candidate.Actions
                    })
                    .ToArray(),
                CustomRouteTemplate = new
                {
                    Label = "",
                    Reason = "",
                    Actions = Array.Empty<object>()
                },
                AnnotationTemplate = new
                {
                    ProtocolVersion = ExpertAnnotation.CurrentProtocolVersion,
                    snapshot.StateId,
                    ReviewerId = "",
                    ReviewedAtUtc = (DateTimeOffset?)null,
                    ExpertTop3 = Array.Empty<object>()
                }
            })
            .ToArray()
    };

    private static IOrderedEnumerable<ExpertReviewCandidate> BlindOrder(SnapshotEvaluation snapshot) =>
        snapshot.ReviewCandidates.OrderBy(
            candidate => BlindSortKey(snapshot.StateId, candidate.CandidateId),
            StringComparer.Ordinal);

    private static string BlindSortKey(string stateId, string candidateId)
    {
        using var sha256 = SHA256.Create();
        return string.Concat(sha256.ComputeHash(Encoding.UTF8.GetBytes(stateId + "\n" + candidateId))
            .Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
    }

    private static string FormatPercent(double value) => value.ToString("P2", CultureInfo.InvariantCulture);

    private static string FormatOptionalPercent(double? value) => value.HasValue ? FormatPercent(value.Value) : "N/A";

    private static string EscapeCell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}
