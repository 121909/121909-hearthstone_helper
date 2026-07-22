using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace DiscardAdvisor.Replay;

public sealed class ExpertAnnotationDraftWriter
{
    public const long MaximumReviewPackBytes = 64L * 1024 * 1024;

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Include
    };

    public string Write(
        string reviewPackPath,
        string stateId,
        IEnumerable<string> rankedOptionIds,
        string outputDirectory,
        bool overwrite = false)
    {
        if (string.IsNullOrWhiteSpace(reviewPackPath))
            throw new ArgumentException("A review pack path is required.", nameof(reviewPackPath));
        if (string.IsNullOrWhiteSpace(stateId))
            throw new ArgumentException("A state id is required.", nameof(stateId));
        if (rankedOptionIds is null)
            throw new ArgumentNullException(nameof(rankedOptionIds));
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("An annotation output directory is required.", nameof(outputDirectory));

        var reviewPack = ReadReviewPack(reviewPackPath);
        var optionIds = rankedOptionIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        if (optionIds.Length is < 1 or > 3)
            throw new InvalidOperationException("Select one to three ranked review options.");
        if (optionIds.Distinct(StringComparer.Ordinal).Count() != optionIds.Length)
            throw new InvalidOperationException("Ranked review options must be unique.");

        var pending = reviewPack["pending"] as JArray ??
                      throw new InvalidDataException("The review pack requires a pending array.");
        var review = pending.OfType<JObject>().SingleOrDefault(item =>
            string.Equals(item.Value<string>("stateId"), stateId, StringComparison.Ordinal)) ??
                     throw new InvalidOperationException($"State '{stateId}' is not pending in the review pack.");
        var options = (review["options"] as JArray)?.OfType<JObject>()
            .ToDictionary(
                option => RequiredString(option, "reviewOptionId"),
                option => option,
                StringComparer.Ordinal) ??
                      throw new InvalidDataException("The pending review requires an options array.");

        var routes = new JArray();
        for (var index = 0; index < optionIds.Length; index++)
        {
            if (!options.TryGetValue(optionIds[index], out var option))
                throw new InvalidOperationException($"Review option '{optionIds[index]}' does not exist for state '{stateId}'.");
            var reviewActions = option["actions"] as JArray ??
                                throw new InvalidDataException($"Review option '{optionIds[index]}' requires actions.");
            var actions = new JArray(reviewActions.OfType<JObject>().Select(action =>
                action["annotation"] is JObject annotation
                    ? annotation.DeepClone()
                    : throw new InvalidDataException($"Review option '{optionIds[index]}' contains an invalid action.")));
            if (actions.Count == 0 || actions.Count != reviewActions.Count)
                throw new InvalidDataException($"Review option '{optionIds[index]}' contains invalid or empty actions.");
            routes.Add(new JObject
            {
                ["label"] = $"Expert rank {index + 1}",
                ["reason"] = "Selected during blind route review.",
                ["actions"] = actions
            });
        }

        var document = new JObject
        {
            ["protocolVersion"] = "1.0.0",
            ["stateId"] = stateId,
            ["expertTop3"] = routes
        };
        var annotation = document.ToObject<ExpertAnnotation>() ??
                         throw new JsonSerializationException("Generated annotation JSON produced null.");
        annotation.Validate();

        var directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        var safeStateId = string.Concat(stateId.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_'));
        var path = Path.Combine(directory, safeStateId + ".annotation.json");
        if (File.Exists(path) && !overwrite)
            throw new IOException($"Annotation '{path}' already exists. Pass overwrite to replace it.");
        var temporaryPath = path + ".tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonConvert.SerializeObject(annotation, JsonSettings) + Environment.NewLine,
                new UTF8Encoding(false));
            if (File.Exists(path))
                File.Replace(temporaryPath, path, null);
            else
                File.Move(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
        return path;
    }

    private static JObject ReadReviewPack(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            throw new FileNotFoundException("Expert review pack was not found.", fullPath);
        if (info.Length > MaximumReviewPackBytes)
            throw new InvalidDataException("Expert review pack exceeds 64 MiB.");
        var document = JObject.Parse(File.ReadAllText(fullPath, Encoding.UTF8));
        if (!string.Equals(document.Value<string>("protocolVersion"), "1.0.0", StringComparison.Ordinal) ||
            !string.Equals(document.Value<string>("reviewMethod"), "BLIND_ROUTE_RANKING", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Unsupported expert review pack protocol or review method.");
        }
        return document;
    }

    private static string RequiredString(JObject value, string name) =>
        value.Value<string>(name) is { Length: > 0 } result
            ? result
            : throw new InvalidDataException($"Review option field '{name}' is required.");
}
