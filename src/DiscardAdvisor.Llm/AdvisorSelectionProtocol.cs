using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DiscardAdvisor.Llm;

public sealed class AdvisorProtocolContext
{
    public AdvisorProtocolContext(
        string stateId,
        string candidateSetHash,
        IEnumerable<string> candidateIds,
        Func<string, bool>? isCandidateStillValid = null)
    {
        if (string.IsNullOrWhiteSpace(stateId) || stateId.Length > 128)
            throw new ArgumentException("A state id is required.", nameof(stateId));
        if (candidateSetHash is null || candidateSetHash.Length != 64 ||
            candidateSetHash.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("A lowercase SHA-256 candidate set hash is required.", nameof(candidateSetHash));
        }
        if (candidateIds is null)
            throw new ArgumentNullException(nameof(candidateIds));
        var frozenIds = candidateIds.ToImmutableHashSet(StringComparer.Ordinal);
        if (frozenIds.Count == 0 || frozenIds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one valid candidate id is required.", nameof(candidateIds));
        StateId = stateId;
        CandidateSetHash = candidateSetHash;
        CandidateIds = frozenIds;
        IsCandidateStillValid = isCandidateStillValid;
    }

    public string StateId { get; }
    public string CandidateSetHash { get; }
    public ImmutableHashSet<string> CandidateIds { get; }
    public Func<string, bool>? IsCandidateStillValid { get; }
}

public sealed record AdvisorSelection
{
    public AdvisorSelection(
        string selectedCandidateId,
        string? alternativeCandidateId,
        double confidence,
        string summary,
        string risk)
    {
        SelectedCandidateId = selectedCandidateId;
        AlternativeCandidateId = alternativeCandidateId;
        Confidence = confidence;
        Summary = summary;
        Risk = risk;
    }

    public string SelectedCandidateId { get; }
    public string? AlternativeCandidateId { get; }
    public double Confidence { get; }
    public string Summary { get; }
    public string Risk { get; }
}

public sealed record AdvisorProtocolError(string Code, string Path, string Message);

public sealed class AdvisorProtocolValidationResult
{
    private AdvisorProtocolValidationResult(
        AdvisorSelection? selection,
        ImmutableArray<AdvisorProtocolError> errors)
    {
        Selection = selection;
        Errors = errors;
    }

    public bool IsValid => Selection is not null && Errors.IsEmpty;
    public AdvisorSelection? Selection { get; }
    public ImmutableArray<AdvisorProtocolError> Errors { get; }

    public static AdvisorProtocolValidationResult Valid(AdvisorSelection selection) =>
        new(selection ?? throw new ArgumentNullException(nameof(selection)), ImmutableArray<AdvisorProtocolError>.Empty);

    public static AdvisorProtocolValidationResult Invalid(params AdvisorProtocolError[] errors) =>
        new(null, errors.ToImmutableArray());
}

public static class AdvisorSelectionProtocol
{
    public const int MaximumResponseCharacters = 8192;
    private static readonly ImmutableHashSet<string> AllowedProperties = new[]
    {
        "protocolVersion",
        "stateId",
        "candidateSetHash",
        "selectedCandidateId",
        "alternativeCandidateId",
        "confidence",
        "summary",
        "risk"
    }.ToImmutableHashSet(StringComparer.Ordinal);

    public static ModelResponseContract ResponseContract { get; } = new(
        "discard_advisor_selection_v1",
        "{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"protocolVersion\",\"stateId\",\"candidateSetHash\",\"selectedCandidateId\",\"confidence\",\"summary\",\"risk\"],\"properties\":{\"protocolVersion\":{\"const\":\"1.0.0\"},\"stateId\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128},\"candidateSetHash\":{\"type\":\"string\",\"pattern\":\"^[a-f0-9]{64}$\"},\"selectedCandidateId\":{\"type\":\"string\",\"minLength\":1},\"alternativeCandidateId\":{\"type\":\"string\",\"minLength\":1},\"confidence\":{\"type\":\"number\",\"minimum\":0,\"maximum\":1},\"summary\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":512},\"risk\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":512}}}");

    public static AdvisorProtocolValidationResult Validate(string json, AdvisorProtocolContext context)
    {
        if (context is null)
            throw new ArgumentNullException(nameof(context));
        if (string.IsNullOrWhiteSpace(json))
            return Error("empty_response", "$", "The model response is empty.");
        if (json.Length > MaximumResponseCharacters)
            return Error("response_too_large", "$", "The model response exceeds the size limit.");

        JObject document;
        try
        {
            document = JObject.Parse(json, new JsonLoadSettings
            {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                CommentHandling = CommentHandling.Ignore,
                LineInfoHandling = LineInfoHandling.Ignore
            });
        }
        catch (JsonException exception)
        {
            return Error("invalid_json", "$", exception.Message);
        }

        var unknown = document.Properties().FirstOrDefault(property => !AllowedProperties.Contains(property.Name));
        if (unknown is not null)
            return Error("unknown_property", "$." + unknown.Name, "Unexpected response property.");
        if (!TryRequiredString(document, "protocolVersion", 16, out var protocolVersion, out var error))
            return error!;
        if (protocolVersion != AdvisorPromptCompressor.ProtocolVersion)
            return Error("protocol_mismatch", "$.protocolVersion", "The protocol version is not supported.");
        if (!TryRequiredString(document, "stateId", 128, out var stateId, out error))
            return error!;
        if (!string.Equals(stateId, context.StateId, StringComparison.Ordinal))
            return Error("stale_state", "$.stateId", "The response belongs to a different state.");
        if (!TryRequiredString(document, "candidateSetHash", 64, out var candidateSetHash, out error))
            return error!;
        if (!string.Equals(candidateSetHash, context.CandidateSetHash, StringComparison.Ordinal))
            return Error("candidate_set_mismatch", "$.candidateSetHash", "The candidate set has changed.");
        if (!TryRequiredString(document, "selectedCandidateId", 128, out var selected, out error))
            return error!;
        if (!context.CandidateIds.Contains(selected!))
            return Error("unknown_candidate", "$.selectedCandidateId", "The selected candidate was not supplied.");
        if (context.IsCandidateStillValid is not null && !context.IsCandidateStillValid(selected!))
            return Error("candidate_no_longer_valid", "$.selectedCandidateId", "The selected candidate is no longer valid.");

        string? alternative = null;
        if (document.TryGetValue("alternativeCandidateId", StringComparison.Ordinal, out var alternativeToken))
        {
            if (alternativeToken.Type != JTokenType.String || string.IsNullOrWhiteSpace(alternativeToken.Value<string>()))
                return Error("invalid_field", "$.alternativeCandidateId", "The alternative candidate id must be a string.");
            alternative = alternativeToken.Value<string>();
            if (!context.CandidateIds.Contains(alternative!))
                return Error("unknown_candidate", "$.alternativeCandidateId", "The alternative candidate was not supplied.");
            if (string.Equals(alternative, selected, StringComparison.Ordinal))
                return Error("duplicate_candidate", "$.alternativeCandidateId", "The alternative must differ from the selection.");
            if (context.IsCandidateStillValid is not null && !context.IsCandidateStillValid(alternative!))
                return Error("candidate_no_longer_valid", "$.alternativeCandidateId", "The alternative candidate is no longer valid.");
        }

        if (!TryConfidence(document, out var confidence, out error))
            return error!;
        if (!TryRequiredString(document, "summary", 512, out var summary, out error))
            return error!;
        if (!TryRequiredString(document, "risk", 512, out var risk, out error))
            return error!;
        return AdvisorProtocolValidationResult.Valid(new AdvisorSelection(
            selected!,
            alternative,
            confidence,
            summary!,
            risk!));
    }

    private static bool TryRequiredString(
        JObject document,
        string propertyName,
        int maximumLength,
        out string? value,
        out AdvisorProtocolValidationResult? error)
    {
        value = null;
        error = null;
        if (!document.TryGetValue(propertyName, StringComparison.Ordinal, out var token) || token.Type != JTokenType.String)
        {
            error = Error("missing_or_invalid_field", "$." + propertyName, "A string value is required.");
            return false;
        }
        value = token.Value<string>();
        if (value is null || string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            error = Error("invalid_field", "$." + propertyName, "The string is empty or exceeds its length limit.");
            return false;
        }
        return true;
    }

    private static bool TryConfidence(
        JObject document,
        out double confidence,
        out AdvisorProtocolValidationResult? error)
    {
        confidence = 0;
        error = null;
        if (!document.TryGetValue("confidence", StringComparison.Ordinal, out var token) ||
            token.Type is not (JTokenType.Float or JTokenType.Integer))
        {
            error = Error("missing_or_invalid_field", "$.confidence", "A numeric confidence is required.");
            return false;
        }
        confidence = token.Value<double>();
        if (double.IsNaN(confidence) || double.IsInfinity(confidence) || confidence < 0 || confidence > 1)
        {
            error = Error("invalid_field", "$.confidence", "Confidence must be between zero and one.");
            return false;
        }
        return true;
    }

    private static AdvisorProtocolValidationResult Error(string code, string path, string message) =>
        AdvisorProtocolValidationResult.Invalid(new AdvisorProtocolError(code, path, message));
}
