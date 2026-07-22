using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DiscardAdvisor.Llm;

public enum ModelMessageRole
{
    System,
    User,
    Assistant
}

public enum ModelFinishReason
{
    Completed,
    OutputLimit,
    ContentFiltered,
    ToolCall,
    Unknown
}

public enum ModelProviderErrorKind
{
    Cancelled,
    Timeout,
    RateLimited,
    Unauthorized,
    InvalidRequest,
    Unavailable,
    MalformedResponse,
    Unknown
}

public sealed record ModelMessage
{
    public ModelMessage(ModelMessageRole role, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Message content is required.", nameof(content));
        Role = role;
        Content = content;
    }

    public ModelMessageRole Role { get; }

    public string Content { get; }
}

public sealed record ModelResponseContract
{
    public ModelResponseContract(string name, string jsonSchema, bool strict = true)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A response contract name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(jsonSchema))
            throw new ArgumentException("A JSON Schema is required.", nameof(jsonSchema));
        Name = name;
        JsonSchema = jsonSchema;
        Strict = strict;
    }

    public string Name { get; }

    public string JsonSchema { get; }

    public bool Strict { get; }
}

public sealed class ModelProviderCapabilities
{
    public ModelProviderCapabilities(
        bool supportsStructuredOutput,
        bool supportsRequestCancellation,
        bool reportsTokenUsage,
        bool reportsRetryAfter)
    {
        SupportsStructuredOutput = supportsStructuredOutput;
        SupportsRequestCancellation = supportsRequestCancellation;
        ReportsTokenUsage = reportsTokenUsage;
        ReportsRetryAfter = reportsRetryAfter;
    }

    public bool SupportsStructuredOutput { get; }

    public bool SupportsRequestCancellation { get; }

    public bool ReportsTokenUsage { get; }

    public bool ReportsRetryAfter { get; }
}

public sealed class ModelProviderRequest
{
    public ModelProviderRequest(
        string requestId,
        IEnumerable<ModelMessage> messages,
        ModelResponseContract responseContract,
        TimeSpan timeout,
        int maxOutputTokens = 512,
        double temperature = 0)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            throw new ArgumentException("A request id is required.", nameof(requestId));
        if (messages is null)
            throw new ArgumentNullException(nameof(messages));
        if (responseContract is null)
            throw new ArgumentNullException(nameof(responseContract));
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (maxOutputTokens < 1)
            throw new ArgumentOutOfRangeException(nameof(maxOutputTokens));
        if (double.IsNaN(temperature) || double.IsInfinity(temperature) || temperature < 0 || temperature > 2)
            throw new ArgumentOutOfRangeException(nameof(temperature));

        var frozenMessages = messages.ToImmutableArray();
        if (frozenMessages.IsEmpty)
            throw new ArgumentException("At least one message is required.", nameof(messages));
        if (frozenMessages.Any(message => message is null))
            throw new ArgumentException("Messages cannot contain null entries.", nameof(messages));

        RequestId = requestId;
        Messages = frozenMessages;
        ResponseContract = responseContract;
        Timeout = timeout;
        MaxOutputTokens = maxOutputTokens;
        Temperature = temperature;
    }

    public string RequestId { get; }

    public ImmutableArray<ModelMessage> Messages { get; }

    public ModelResponseContract ResponseContract { get; }

    public TimeSpan Timeout { get; }

    public int MaxOutputTokens { get; }

    public double Temperature { get; }
}

public sealed record ModelTokenUsage
{
    public ModelTokenUsage(int inputTokens, int outputTokens)
    {
        if (inputTokens < 0)
            throw new ArgumentOutOfRangeException(nameof(inputTokens));
        if (outputTokens < 0)
            throw new ArgumentOutOfRangeException(nameof(outputTokens));
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
    }

    public int InputTokens { get; }

    public int OutputTokens { get; }

    public int TotalTokens => InputTokens + OutputTokens;
}

public sealed record ModelProviderResponse
{
    public ModelProviderResponse(
        string content,
        ModelFinishReason finishReason,
        TimeSpan latency,
        string? providerRequestId = null,
        ModelTokenUsage? tokenUsage = null)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Response content is required.", nameof(content));
        if (latency < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(latency));
        Content = content;
        FinishReason = finishReason;
        Latency = latency;
        ProviderRequestId = providerRequestId;
        TokenUsage = tokenUsage;
    }

    public string Content { get; }

    public ModelFinishReason FinishReason { get; }

    public TimeSpan Latency { get; }

    public string? ProviderRequestId { get; }

    public ModelTokenUsage? TokenUsage { get; }
}

public sealed record ModelProviderError
{
    public ModelProviderError(
        ModelProviderErrorKind kind,
        string code,
        string message,
        bool isRetryable,
        TimeSpan? retryAfter = null)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("An error code is required.", nameof(code));
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("An error message is required.", nameof(message));
        if (retryAfter < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryAfter));
        Kind = kind;
        Code = code;
        Message = message;
        IsRetryable = isRetryable;
        RetryAfter = retryAfter;
    }

    public ModelProviderErrorKind Kind { get; }

    public string Code { get; }

    public string Message { get; }

    public bool IsRetryable { get; }

    public TimeSpan? RetryAfter { get; }
}

public sealed class ModelProviderResult
{
    private ModelProviderResult(ModelProviderResponse? response, ModelProviderError? error)
    {
        Response = response;
        Error = error;
    }

    public bool IsSuccess => Response is not null;

    public ModelProviderResponse? Response { get; }

    public ModelProviderError? Error { get; }

    public static ModelProviderResult Success(ModelProviderResponse response) =>
        new(response ?? throw new ArgumentNullException(nameof(response)), null);

    public static ModelProviderResult Failure(ModelProviderError error) =>
        new(null, error ?? throw new ArgumentNullException(nameof(error)));
}

public interface IModelProvider
{
    string ProviderId { get; }

    string ModelId { get; }

    ModelProviderCapabilities Capabilities { get; }

    Task<ModelProviderResult> CompleteAsync(
        ModelProviderRequest request,
        CancellationToken cancellationToken = default);
}
