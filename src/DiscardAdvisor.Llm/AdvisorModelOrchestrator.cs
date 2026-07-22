using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DiscardAdvisor.Llm;

public enum AdvisorRerankStatus
{
    ModelSelected,
    LocalFallback,
    Stale
}

public enum AdvisorDecisionSource
{
    Model,
    Cache,
    Local
}

public sealed class AdvisorRerankResult
{
    internal AdvisorRerankResult(
        AdvisorRerankStatus status,
        AdvisorDecisionSource source,
        string? selectedCandidateId,
        string? alternativeCandidateId,
        double? confidence,
        string? summary,
        string? risk,
        int attempts,
        ModelProviderError? error)
    {
        Status = status;
        Source = source;
        SelectedCandidateId = selectedCandidateId;
        AlternativeCandidateId = alternativeCandidateId;
        Confidence = confidence;
        Summary = summary;
        Risk = risk;
        Attempts = attempts;
        Error = error;
    }

    public AdvisorRerankStatus Status { get; }
    public AdvisorDecisionSource Source { get; }
    public string? SelectedCandidateId { get; }
    public string? AlternativeCandidateId { get; }
    public double? Confidence { get; }
    public string? Summary { get; }
    public string? Risk { get; }
    public int Attempts { get; }
    public ModelProviderError? Error { get; }
}

public sealed class AdvisorModelExecutionOptions
{
    public AdvisorModelExecutionOptions(
        int maximumAttempts = 2,
        TimeSpan? retryBaseDelay = null,
        TimeSpan? maximumRetryDelay = null,
        TimeSpan? cacheTimeToLive = null,
        int maxOutputTokens = 384)
    {
        if (maximumAttempts < 1 || maximumAttempts > 5)
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        RetryBaseDelay = retryBaseDelay ?? TimeSpan.FromMilliseconds(100);
        MaximumRetryDelay = maximumRetryDelay ?? TimeSpan.FromSeconds(1);
        CacheTimeToLive = cacheTimeToLive ?? TimeSpan.FromMinutes(5);
        if (RetryBaseDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryBaseDelay));
        if (MaximumRetryDelay < TimeSpan.Zero || MaximumRetryDelay < RetryBaseDelay)
            throw new ArgumentOutOfRangeException(nameof(maximumRetryDelay));
        if (CacheTimeToLive <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cacheTimeToLive));
        if (maxOutputTokens < 1)
            throw new ArgumentOutOfRangeException(nameof(maxOutputTokens));
        MaximumAttempts = maximumAttempts;
        MaxOutputTokens = maxOutputTokens;
    }

    public int MaximumAttempts { get; }
    public TimeSpan RetryBaseDelay { get; }
    public TimeSpan MaximumRetryDelay { get; }
    public TimeSpan CacheTimeToLive { get; }
    public int MaxOutputTokens { get; }
}

public interface IAdvisorSelectionCache
{
    bool TryGet(string key, out AdvisorSelection? selection);

    void Set(string key, AdvisorSelection selection, TimeSpan timeToLive);

    void Remove(string key);
}

public sealed class MemoryAdvisorSelectionCache : IAdvisorSelectionCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly int _maximumEntries;
    private readonly Func<DateTimeOffset> _utcNow;

    public MemoryAdvisorSelectionCache(
        int maximumEntries = 128,
        Func<DateTimeOffset>? utcNow = null)
    {
        if (maximumEntries < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        _maximumEntries = maximumEntries;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public bool TryGet(string key, out AdvisorSelection? selection)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("A cache key is required.", nameof(key));
        selection = null;
        if (!_entries.TryGetValue(key, out var entry))
            return false;
        if (entry.ExpiresAt <= _utcNow())
        {
            _entries.TryRemove(key, out _);
            return false;
        }
        selection = entry.Selection;
        return true;
    }

    public void Set(string key, AdvisorSelection selection, TimeSpan timeToLive)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("A cache key is required.", nameof(key));
        if (selection is null)
            throw new ArgumentNullException(nameof(selection));
        if (timeToLive <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeToLive));
        var now = _utcNow();
        RemoveExpired(now);
        if (_entries.Count >= _maximumEntries && !_entries.ContainsKey(key))
        {
            var oldest = _entries.OrderBy(entry => entry.Value.CreatedAt).FirstOrDefault();
            if (!string.IsNullOrEmpty(oldest.Key))
                _entries.TryRemove(oldest.Key, out _);
        }
        _entries[key] = new CacheEntry(selection, now, now + timeToLive);
    }

    public void Remove(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;
        _entries.TryRemove(key, out _);
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var entry in _entries.Where(entry => entry.Value.ExpiresAt <= now))
            _entries.TryRemove(entry.Key, out _);
    }

    private sealed class CacheEntry
    {
        public CacheEntry(AdvisorSelection selection, DateTimeOffset createdAt, DateTimeOffset expiresAt)
        {
            Selection = selection;
            CreatedAt = createdAt;
            ExpiresAt = expiresAt;
        }

        public AdvisorSelection Selection { get; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset ExpiresAt { get; }
    }
}

public sealed class AdvisorModelOrchestrator
{
    private readonly IModelProvider _provider;
    private readonly AdvisorPromptCompressor _compressor;
    private readonly IAdvisorSelectionCache _cache;
    private readonly AdvisorModelExecutionOptions _options;

    public AdvisorModelOrchestrator(
        IModelProvider provider,
        AdvisorPromptCompressor? compressor = null,
        IAdvisorSelectionCache? cache = null,
        AdvisorModelExecutionOptions? options = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        if (string.IsNullOrWhiteSpace(provider.ProviderId))
            throw new ArgumentException("The provider must expose an id.", nameof(provider));
        if (string.IsNullOrWhiteSpace(provider.ModelId))
            throw new ArgumentException("The provider must expose a model id.", nameof(provider));
        _compressor = compressor ?? new AdvisorPromptCompressor();
        _cache = cache ?? new MemoryAdvisorSelectionCache();
        _options = options ?? new AdvisorModelExecutionOptions();
    }

    public async Task<AdvisorRerankResult> RerankAsync(
        string requestId,
        CompressedAdvisorPrompt prompt,
        TimeSpan requestTimeout,
        Func<string, bool>? isStateCurrent = null,
        Func<string, bool>? isCandidateStillValid = null,
        CancellationToken cancellationToken = default)
    {
        if (prompt is null)
            throw new ArgumentNullException(nameof(prompt));
        if (requestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsStateCurrent(prompt.StateId, isStateCurrent))
            return Stale();

        var cacheKey = CacheKey(prompt);
        if (_cache.TryGet(cacheKey, out var cached) && cached is not null)
        {
            if (IsSelectionCurrent(cached, prompt, isCandidateStillValid))
            {
                return ModelSelection(cached, AdvisorDecisionSource.Cache, 0);
            }
            _cache.Remove(cacheKey);
        }

        var request = _compressor.CreateProviderRequest(
            requestId,
            prompt,
            requestTimeout,
            _options.MaxOutputTokens);
        ModelProviderError? lastError = null;
        var attemptsPerformed = 0;
        for (var attempt = 1; attempt <= _options.MaximumAttempts; attempt++)
        {
            attemptsPerformed = attempt;
            cancellationToken.ThrowIfCancellationRequested();
            var providerResult = await InvokeWithTimeoutAsync(request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsStateCurrent(prompt.StateId, isStateCurrent))
                return Stale();

            if (providerResult is null)
            {
                lastError = new ModelProviderError(
                    ModelProviderErrorKind.Unknown,
                    "provider_returned_null_result",
                    "The model provider returned no result.",
                    isRetryable: false);
            }
            else if (providerResult.IsSuccess && providerResult.Response is not null)
            {
                var finishError = FinishReasonError(providerResult.Response.FinishReason);
                if (finishError is not null)
                {
                    lastError = finishError;
                }
                else
                {
                    var validation = AdvisorSelectionProtocol.Validate(
                        providerResult.Response.Content,
                        new AdvisorProtocolContext(
                            prompt.StateId,
                            prompt.CandidateSetHash,
                            prompt.CandidateIds,
                            candidateId => IsCandidateValid(candidateId, isCandidateStillValid)));
                    if (validation.IsValid && validation.Selection is not null)
                    {
                        _cache.Set(cacheKey, validation.Selection, _options.CacheTimeToLive);
                        return ModelSelection(validation.Selection, AdvisorDecisionSource.Model, attempt);
                    }
                    var validationError = validation.Errors.FirstOrDefault();
                    lastError = new ModelProviderError(
                        ModelProviderErrorKind.MalformedResponse,
                        validationError?.Code ?? "invalid_model_response",
                        validationError?.Message ?? "The model response failed protocol validation.",
                        isRetryable: validationError?.Code != "candidate_no_longer_valid");
                }
            }
            else
            {
                lastError = providerResult.Error ?? new ModelProviderError(
                    ModelProviderErrorKind.Unknown,
                    "provider_failed_without_error",
                    "The model provider failed without an error payload.",
                    isRetryable: false);
            }

            if (attempt >= _options.MaximumAttempts || lastError is null || !lastError.IsRetryable)
                break;
            await Task.Delay(RetryDelay(lastError, attempt), cancellationToken).ConfigureAwait(false);
        }

        return LocalFallback(prompt, isCandidateStillValid, lastError, attemptsPerformed);
    }

    private async Task<ModelProviderResult> InvokeWithTimeoutAsync(
        ModelProviderRequest request,
        CancellationToken cancellationToken)
    {
        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<ModelProviderResult> providerTask;
        try
        {
            providerTask = _provider.CompleteAsync(request, attemptCancellation.Token);
            if (providerTask is null)
            {
                return ModelProviderResult.Failure(new ModelProviderError(
                    ModelProviderErrorKind.Unknown,
                    "provider_returned_null_task",
                    "The model provider returned no operation.",
                    isRetryable: false));
            }
        }
        catch (Exception exception)
        {
            return ProviderException(exception, cancellationToken);
        }

        var timeoutTask = Task.Delay(request.Timeout, attemptCancellation.Token);
        var completed = await Task.WhenAny(providerTask, timeoutTask).ConfigureAwait(false);
        if (completed != providerTask)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attemptCancellation.Cancel();
            ObserveLateFault(providerTask);
            return ModelProviderResult.Failure(new ModelProviderError(
                ModelProviderErrorKind.Timeout,
                "provider_timeout",
                "The model provider exceeded the request timeout.",
                isRetryable: true));
        }

        try
        {
            attemptCancellation.Cancel();
            return await providerTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return ProviderException(exception, cancellationToken);
        }
    }

    private static ModelProviderResult ProviderException(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            cancellationToken.ThrowIfCancellationRequested();
        var kind = exception is OperationCanceledException
            ? ModelProviderErrorKind.Timeout
            : ModelProviderErrorKind.Unknown;
        return ModelProviderResult.Failure(new ModelProviderError(
            kind,
            exception is OperationCanceledException ? "provider_timeout" : "provider_exception",
            "The model provider did not complete successfully.",
            isRetryable: exception is OperationCanceledException));
    }

    private static void ObserveLateFault(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static ModelProviderError? FinishReasonError(ModelFinishReason finishReason) => finishReason switch
    {
        ModelFinishReason.Completed => null,
        ModelFinishReason.OutputLimit => new ModelProviderError(
            ModelProviderErrorKind.MalformedResponse,
            "model_output_limit",
            "The model response reached its output limit.",
            false),
        ModelFinishReason.ContentFiltered => new ModelProviderError(
            ModelProviderErrorKind.InvalidRequest,
            "model_content_filtered",
            "The model response was filtered.",
            false),
        _ => new ModelProviderError(
            ModelProviderErrorKind.MalformedResponse,
            "model_incomplete_response",
            "The model did not return a completed response.",
            true)
    };

    private TimeSpan RetryDelay(ModelProviderError error, int attempt)
    {
        var multiplier = Math.Pow(2, Math.Max(0, attempt - 1));
        var exponentialTicks = Math.Min(
            _options.MaximumRetryDelay.Ticks,
            (long)Math.Min(long.MaxValue, _options.RetryBaseDelay.Ticks * multiplier));
        var delay = TimeSpan.FromTicks(exponentialTicks);
        if (error.RetryAfter is TimeSpan retryAfter && retryAfter > delay)
            delay = retryAfter > _options.MaximumRetryDelay ? _options.MaximumRetryDelay : retryAfter;
        return delay;
    }

    private string CacheKey(CompressedAdvisorPrompt prompt) => string.Join(
        "|",
        _provider.ProviderId,
        _provider.ModelId,
        AdvisorPromptCompressor.ProtocolVersion,
        prompt.StateId,
        prompt.CandidateSetHash);

    private static bool IsStateCurrent(string stateId, Func<string, bool>? validator)
    {
        if (validator is null)
            return true;
        try
        {
            return validator(stateId);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCandidateValid(string candidateId, Func<string, bool>? validator)
    {
        if (validator is null)
            return true;
        try
        {
            return validator(candidateId);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSelectionCurrent(
        AdvisorSelection selection,
        CompressedAdvisorPrompt prompt,
        Func<string, bool>? validator) =>
        prompt.CandidateIds.Contains(selection.SelectedCandidateId, StringComparer.Ordinal) &&
        IsCandidateValid(selection.SelectedCandidateId, validator) &&
        (selection.AlternativeCandidateId is null ||
         prompt.CandidateIds.Contains(selection.AlternativeCandidateId, StringComparer.Ordinal) &&
         IsCandidateValid(selection.AlternativeCandidateId, validator));

    private static AdvisorRerankResult ModelSelection(
        AdvisorSelection selection,
        AdvisorDecisionSource source,
        int attempts) => new(
        AdvisorRerankStatus.ModelSelected,
        source,
        selection.SelectedCandidateId,
        selection.AlternativeCandidateId,
        selection.Confidence,
        selection.Summary,
        selection.Risk,
        attempts,
        null);

    private static AdvisorRerankResult LocalFallback(
        CompressedAdvisorPrompt prompt,
        Func<string, bool>? candidateValidator,
        ModelProviderError? error,
        int attempts)
    {
        var fallback = prompt.CandidateIds.FirstOrDefault(candidateId =>
            IsCandidateValid(candidateId, candidateValidator));
        return fallback is null
            ? Stale()
            : new AdvisorRerankResult(
                AdvisorRerankStatus.LocalFallback,
                AdvisorDecisionSource.Local,
                fallback,
                null,
                null,
                null,
                null,
                attempts,
                error);
    }

    private static AdvisorRerankResult Stale() => new(
        AdvisorRerankStatus.Stale,
        AdvisorDecisionSource.Local,
        null,
        null,
        null,
        null,
        null,
        0,
        null);
}
