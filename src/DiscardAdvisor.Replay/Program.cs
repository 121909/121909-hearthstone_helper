using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace DiscardAdvisor.Replay;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
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
            Console.WriteLine($"Shadow games: {report.ShadowRun.CompletedGameCount}/50");
            Console.WriteLine($"Shadow superseded: {report.ShadowRun.SupersededCount}/{report.ShadowRun.AnalysisCount}");
            Console.WriteLine($"Shadow thresholds: {(report.ShadowRun.MeetsAutomatedAcceptanceThresholds ? "MET" : "NOT MET")}");
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
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(CommandLine.Usage);
            return 2;
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
