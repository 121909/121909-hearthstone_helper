using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using DiscardAdvisor.Domain.Snapshots;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace DiscardAdvisor.Replay;

public sealed record SnapshotFixture(string Source, GameSnapshot Snapshot);

public sealed record ReplayArchiveSummary(
    string Source,
    int PowerLogLineCount,
    int SnapshotCount,
    int AnnotationCount);

public sealed record RegressionInputSet(
    ImmutableArray<SnapshotFixture> Snapshots,
    ImmutableDictionary<string, ExpertAnnotation> Annotations,
    ImmutableArray<ReplayArchiveSummary> Replays,
    ImmutableArray<string> Errors);

internal sealed record RawDocument(string Source, string Json);

internal sealed record ReplayArchiveDocuments(
    ReplayArchiveSummary Summary,
    ImmutableArray<RawDocument> Snapshots,
    ImmutableArray<RawDocument> Annotations);

public sealed class HdtReplayArchiveReader
{
    public const long MaximumPowerLogBytes = 128L * 1024 * 1024;
    public const long MaximumFixtureBytes = 2L * 1024 * 1024;
    public const int MaximumFixtureCount = 5000;
    private const string PowerLogEntryName = "output_log.txt";
    private const string EmbeddedSnapshotPrefix = "discard-advisor/snapshots/";
    private const string EmbeddedAnnotationPrefix = "discard-advisor/annotations/";

    internal ReplayArchiveDocuments Read(string path, bool includeSidecar)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A replay path is required.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("HDT replay was not found.", fullPath);

        using var archive = ZipFile.OpenRead(fullPath);
        var powerLog = archive.Entries.FirstOrDefault(entry =>
            string.Equals(NormalizeEntryName(entry.FullName), PowerLogEntryName, StringComparison.OrdinalIgnoreCase));
        if (powerLog is null)
            throw new InvalidDataException("The .hdtreplay archive does not contain output_log.txt.");
        if (powerLog.Length > MaximumPowerLogBytes)
            throw new InvalidDataException("The replay power log exceeds the 128 MiB offline limit.");

        var powerLogLineCount = CountLines(powerLog);
        var snapshots = ImmutableArray.CreateBuilder<RawDocument>();
        var annotations = ImmutableArray.CreateBuilder<RawDocument>();
        foreach (var entry in archive.Entries)
        {
            var entryName = NormalizeEntryName(entry.FullName);
            if (entryName.StartsWith(EmbeddedSnapshotPrefix, StringComparison.OrdinalIgnoreCase) &&
                entryName.EndsWith(".snapshot.json", StringComparison.OrdinalIgnoreCase))
            {
                snapshots.Add(ReadJsonEntry(fullPath, entry));
            }
            else if (entryName.StartsWith(EmbeddedAnnotationPrefix, StringComparison.OrdinalIgnoreCase) &&
                     entryName.EndsWith(".annotation.json", StringComparison.OrdinalIgnoreCase))
            {
                annotations.Add(ReadJsonEntry(fullPath, entry));
            }
        }

        if (includeSidecar)
            ReadSidecar(fullPath, snapshots, annotations);
        if (snapshots.Count > MaximumFixtureCount)
            throw new InvalidDataException($"The replay contains more than {MaximumFixtureCount} snapshot fixtures.");

        var summary = new ReplayArchiveSummary(fullPath, powerLogLineCount, snapshots.Count, annotations.Count);
        return new ReplayArchiveDocuments(summary, snapshots.ToImmutable(), annotations.ToImmutable());
    }

    private static int CountLines(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, false);
        var count = 0;
        while (reader.ReadLine() is not null)
            count++;
        return count;
    }

    private static RawDocument ReadJsonEntry(string archivePath, ZipArchiveEntry entry)
    {
        if (entry.Length > MaximumFixtureBytes)
            throw new InvalidDataException($"Replay fixture '{entry.FullName}' exceeds 2 MiB.");
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, false);
        return new RawDocument($"{archivePath}!/{NormalizeEntryName(entry.FullName)}", reader.ReadToEnd());
    }

    private static void ReadSidecar(
        string replayPath,
        ImmutableArray<RawDocument>.Builder snapshots,
        ImmutableArray<RawDocument>.Builder annotations)
    {
        var directory = Path.Combine(
            Path.GetDirectoryName(replayPath) ?? string.Empty,
            Path.GetFileNameWithoutExtension(replayPath) + ".snapshots");
        if (!Directory.Exists(directory))
            return;
        foreach (var path in Directory.EnumerateFiles(directory, "*.snapshot.json", SearchOption.AllDirectories))
            snapshots.Add(ReadJsonFile(path));
        foreach (var path in Directory.EnumerateFiles(directory, "*.annotation.json", SearchOption.AllDirectories))
            annotations.Add(ReadJsonFile(path));
    }

    internal static RawDocument ReadJsonFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            throw new FileNotFoundException("Fixture file was not found.", fullPath);
        if (info.Length > MaximumFixtureBytes)
            throw new InvalidDataException($"Fixture '{fullPath}' exceeds 2 MiB.");
        return new RawDocument(fullPath, File.ReadAllText(fullPath, Encoding.UTF8));
    }

    private static string NormalizeEntryName(string value) => value.Replace('\\', '/').TrimStart('/');
}

public sealed class RegressionInputLoader
{
    private static readonly JsonSerializerSettings SnapshotSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        MissingMemberHandling = MissingMemberHandling.Error,
        NullValueHandling = NullValueHandling.Include,
        Converters = { new SnapshotActionJsonConverter() }
    };
    private readonly HdtReplayArchiveReader _replayReader = new();

    public RegressionInputSet Load(IEnumerable<string> inputs)
    {
        if (inputs is null)
            throw new ArgumentNullException(nameof(inputs));
        var paths = inputs.Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath).ToArray();
        if (paths.Length == 0)
            throw new ArgumentException("At least one input path is required.", nameof(inputs));

        var rawSnapshots = new Dictionary<string, RawDocument>(StringComparer.OrdinalIgnoreCase);
        var rawAnnotations = new Dictionary<string, RawDocument>(StringComparer.OrdinalIgnoreCase);
        var replays = ImmutableArray.CreateBuilder<ReplayArchiveSummary>();
        var errors = ImmutableArray.CreateBuilder<string>();
        foreach (var path in Discover(paths, errors))
        {
            try
            {
                if (path.EndsWith(".hdtreplay", StringComparison.OrdinalIgnoreCase))
                {
                    var replay = _replayReader.Read(path, includeSidecar: true);
                    replays.Add(replay.Summary);
                    AddDocuments(rawSnapshots, replay.Snapshots);
                    AddDocuments(rawAnnotations, replay.Annotations);
                }
                else if (path.EndsWith(".annotation.json", StringComparison.OrdinalIgnoreCase))
                {
                    var document = HdtReplayArchiveReader.ReadJsonFile(path);
                    rawAnnotations[document.Source] = document;
                }
                else
                {
                    var document = HdtReplayArchiveReader.ReadJsonFile(path);
                    rawSnapshots[document.Source] = document;
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                errors.Add($"{path}: {exception.Message}");
            }
        }

        var fixturesByStateId = new Dictionary<string, SnapshotFixture>(StringComparer.Ordinal);
        foreach (var document in rawSnapshots.Values.OrderBy(item => item.Source, StringComparer.Ordinal))
        {
            try
            {
                var snapshot = JsonConvert.DeserializeObject<GameSnapshot>(document.Json, SnapshotSettings)
                    ?? throw new JsonSerializationException("Snapshot JSON produced null.");
                ValidateSnapshot(snapshot);
                if (!fixturesByStateId.TryAdd(snapshot.StateId, new SnapshotFixture(document.Source, snapshot)))
                    errors.Add($"{document.Source}: duplicate state_id '{snapshot.StateId}'.");
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
            {
                errors.Add($"{document.Source}: {exception.Message}");
            }
        }

        var annotations = ImmutableDictionary.CreateBuilder<string, ExpertAnnotation>(StringComparer.Ordinal);
        foreach (var document in rawAnnotations.Values.OrderBy(item => item.Source, StringComparer.Ordinal))
        {
            try
            {
                var annotation = JsonConvert.DeserializeObject<ExpertAnnotation>(document.Json, SnapshotSettings)
                    ?? throw new JsonSerializationException("Annotation JSON produced null.");
                annotation.Validate();
                if (!annotations.TryAdd(annotation.StateId, annotation))
                    errors.Add($"{document.Source}: duplicate annotation for state_id '{annotation.StateId}'.");
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
            {
                errors.Add($"{document.Source}: {exception.Message}");
            }
        }

        return new RegressionInputSet(
            fixturesByStateId.Values.OrderBy(fixture => fixture.Snapshot.StateId, StringComparer.Ordinal).ToImmutableArray(),
            annotations.ToImmutable(),
            replays.ToImmutable(),
            errors.ToImmutable());
    }

    private static IEnumerable<string> Discover(IEnumerable<string> inputs, ImmutableArray<string>.Builder errors)
    {
        var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in inputs)
        {
            if (File.Exists(input))
            {
                paths.Add(input);
                continue;
            }
            if (!Directory.Exists(input))
            {
                errors.Add($"{input}: input path does not exist.");
                continue;
            }
            foreach (var pattern in new[] { "*.hdtreplay", "*.snapshot.json", "*.annotation.json" })
            {
                foreach (var path in Directory.EnumerateFiles(input, pattern, SearchOption.AllDirectories))
                    paths.Add(Path.GetFullPath(path));
            }
        }
        return paths;
    }

    private static void AddDocuments(IDictionary<string, RawDocument> destination, IEnumerable<RawDocument> documents)
    {
        foreach (var document in documents)
            destination[document.Source] = document;
    }

    private static void ValidateSnapshot(GameSnapshot snapshot)
    {
        if (!string.Equals(snapshot.ProtocolVersion, "1.0.0", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported snapshot protocol '{snapshot.ProtocolVersion}'.");
        if (string.IsNullOrWhiteSpace(snapshot.StateId))
            throw new InvalidOperationException("Snapshot state_id is required.");
        if (snapshot.Friendly is null || snapshot.Opponent is null || snapshot.Derived is null)
            throw new InvalidOperationException("Snapshot player and derived state fields are required.");
    }

    private sealed class SnapshotActionJsonConverter : JsonConverter
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
            var actionType = value.GetValue("actionType", StringComparison.OrdinalIgnoreCase)?.Value<string>();
            if (string.IsNullOrWhiteSpace(actionType))
                throw new JsonSerializationException("Recorded snapshot action requires actionType.");
            return new RecordedSnapshotAction(actionType);
        }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) =>
            throw new NotSupportedException();
    }

    private sealed class RecordedSnapshotAction : SnapshotAction
    {
        public RecordedSnapshotAction(string actionType)
            : base(actionType)
        {
        }
    }
}
