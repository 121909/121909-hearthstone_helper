using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;

namespace DiscardAdvisor.Replay;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && string.Equals(args[0], "annotate", StringComparison.OrdinalIgnoreCase))
            {
                var annotationCommand = AnnotationCommand.Parse(args.Skip(1).ToArray());
                var annotationPath = new ExpertAnnotationDraftWriter().Write(
                    annotationCommand.ReviewPack,
                    annotationCommand.StateId,
                    annotationCommand.RankedOptions,
                    annotationCommand.ReviewerId,
                    annotationCommand.OutputDirectory,
                    annotationCommand.Overwrite);
                Console.WriteLine($"Expert annotation: {annotationPath}");
                return 0;
            }
            var command = CommandLine.Parse(args);
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            var input = new RegressionInputLoader().Load(command.Inputs);
            var report = new OfflineRegressionRunner().Run(input, command.Options, cancellation.Token);
            var paths = new RegressionReportWriter().Write(report, command.OutputDirectory);
            Console.WriteLine($"Offline regression: {(report.Passed ? "PASS" : "FAIL")}");
            Console.WriteLine($"Snapshots: {report.EvaluatedSnapshotCount}/{report.SnapshotCount}");
            Console.WriteLine($"Legal routes: {report.LegalRouteCount}/{report.RouteCount}");
            Console.WriteLine($"Latency p95: {report.LatencyP95Ms:F2} ms");
            Console.WriteLine($"Shadow games with published analyses: {report.ShadowRun.CompletedGameWithPublishedAnalysisCount}/{ValidationPolicy.RequiredShadowGameCount}");
            Console.WriteLine($"Shadow runs/version cohorts: {report.ShadowRun.RunCount}/{report.ShadowRun.VersionCohortCount}");
            Console.WriteLine($"Shadow requests/analyses: {report.ShadowRun.RequestCount}/{report.ShadowRun.AnalysisCount}");
            Console.WriteLine($"Shadow superseded: {report.ShadowRun.SupersededCount}/{report.ShadowRun.AnalysisCount}");
            Console.WriteLine($"Shadow thresholds: {(report.ShadowRun.MeetsAutomatedAcceptanceThresholds ? "MET" : "NOT MET")}");
            Console.WriteLine($"Visible-test prerequisites: {(report.MeetsVisibleSuggestionPrerequisites ? "MET" : "NOT MET")}");
            Console.WriteLine($"JSON report: {paths.JsonPath}");
            Console.WriteLine($"Markdown report: {paths.MarkdownPath}");
            Console.WriteLine($"Expert review pack: {paths.ExpertReviewPath}");
            return report.Passed ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Offline regression cancelled.");
            return 130;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(CommandLine.Usage);
            Console.Error.WriteLine(AnnotationCommand.Usage);
            return 2;
        }
    }

    private sealed record AnnotationCommand(
        string ReviewPack,
        string StateId,
        IReadOnlyList<string> RankedOptions,
        string ReviewerId,
        string OutputDirectory,
        bool Overwrite)
    {
        public const string Usage =
            "Usage: DiscardAdvisor.Replay annotate --review-pack <path> --state-id <id> " +
            "--reviewer-id <anonymous-id> --rank <option-id> [--rank <option-id>] [--rank <option-id>] " +
            "[--output <directory>] [--force]";

        public static AnnotationCommand Parse(IReadOnlyList<string> args)
        {
            string? reviewPack = null;
            string? stateId = null;
            string? reviewerId = null;
            string? output = null;
            var ranks = new List<string>();
            var overwrite = false;
            for (var index = 0; index < args.Count; index++)
            {
                var option = args[index];
                if (option == "--force")
                {
                    overwrite = true;
                    continue;
                }
                var value = index + 1 < args.Count
                    ? args[++index]
                    : throw new ArgumentException($"Missing value for '{option}'.");
                switch (option)
                {
                    case "--review-pack":
                        reviewPack = value;
                        break;
                    case "--state-id":
                        stateId = value;
                        break;
                    case "--reviewer-id":
                        reviewerId = value;
                        break;
                    case "--rank":
                        ranks.Add(value);
                        break;
                    case "--output":
                        output = value;
                        break;
                    default:
                        throw new ArgumentException($"Unknown annotation option '{option}'.");
                }
            }
            if (string.IsNullOrWhiteSpace(reviewPack))
                throw new ArgumentException("--review-pack is required.");
            if (string.IsNullOrWhiteSpace(stateId))
                throw new ArgumentException("--state-id is required.");
            if (string.IsNullOrWhiteSpace(reviewerId))
                throw new ArgumentException("--reviewer-id is required.");
            if (ranks.Count is < 1 or > 3)
                throw new ArgumentException("Provide one to three --rank options.");
            output ??= Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(reviewPack)) ?? Environment.CurrentDirectory,
                "annotations");
            return new AnnotationCommand(reviewPack, stateId, ranks, reviewerId, output, overwrite);
        }
    }

    private sealed record CommandLine(
        IReadOnlyList<string> Inputs,
        string OutputDirectory,
        OfflineRegressionOptions Options)
    {
        public const string Usage =
            "Usage: DiscardAdvisor.Replay --input <path> [--input <path>] [--output <directory>] " +
            "[--time-budget-ms <1..10000>] [--beam-width <n>] [--maximum-actions <n>] [--top-k <n>] [--seed <n>]";

        public static CommandLine Parse(IReadOnlyList<string> args)
        {
            var inputs = new List<string>();
            var output = Path.Combine(Environment.CurrentDirectory, ".artifacts", "offline-regression");
            var beamWidth = 64;
            var maximumActions = 12;
            var topK = 5;
            var timeBudgetMs = 250;
            var seed = 0x5EED;
            for (var index = 0; index < args.Count; index++)
            {
                var option = args[index];
                var value = index + 1 < args.Count ? args[++index] : throw new ArgumentException($"Missing value for '{option}'.");
                switch (option)
                {
                    case "--input":
                        inputs.Add(value);
                        break;
                    case "--output":
                        output = value;
                        break;
                    case "--beam-width":
                        beamWidth = ParsePositive(value, option);
                        break;
                    case "--maximum-actions":
                        maximumActions = ParsePositive(value, option);
                        break;
                    case "--top-k":
                        topK = ParsePositive(value, option);
                        break;
                    case "--time-budget-ms":
                        timeBudgetMs = ParsePositive(value, option);
                        if (timeBudgetMs > 10000)
                            throw new ArgumentOutOfRangeException(option, "Time budget cannot exceed 10000 ms.");
                        break;
                    case "--seed":
                        seed = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                            ? parsed
                            : throw new ArgumentException($"'{value}' is not a valid integer for '{option}'.");
                        break;
                    default:
                        throw new ArgumentException($"Unknown option '{option}'.");
                }
            }
            if (inputs.Count == 0)
                throw new ArgumentException("At least one --input path is required.");
            return new CommandLine(
                inputs,
                output,
                new OfflineRegressionOptions(beamWidth, maximumActions, topK, timeBudgetMs, seed));
        }

        private static int ParsePositive(string value, string option) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed
                : throw new ArgumentException($"'{value}' is not a positive integer for '{option}'.");
    }
}
