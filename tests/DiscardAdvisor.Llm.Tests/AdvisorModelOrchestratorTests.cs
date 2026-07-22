using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DiscardAdvisor.Rules.Model;
using DiscardAdvisor.Search;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DiscardAdvisor.Llm.Tests;

public sealed class AdvisorModelOrchestratorTests
{
    [Fact]
    public async Task ValidatedSelectionIsCachedByStateCandidateSetAndModel()
    {
        var prompt = Prompt();
        var provider = new ScriptedProvider();
        provider.Enqueue(ValidSelection);
        var orchestrator = Orchestrator(provider);

        var first = await orchestrator.RerankAsync("request-1", prompt, TimeSpan.FromSeconds(1));
        var second = await orchestrator.RerankAsync("request-2", prompt, TimeSpan.FromSeconds(1));

        Assert.Equal(AdvisorRerankStatus.ModelSelected, first.Status);
        Assert.Equal(AdvisorDecisionSource.Model, first.Source);
        Assert.Equal(prompt.CandidateIds[0], first.SelectedCandidateId);
        Assert.Equal(1, first.Attempts);
        Assert.Equal(AdvisorDecisionSource.Cache, second.Source);
        Assert.Equal(0, second.Attempts);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task RetryableFailureRetriesAndThenAcceptsValidResponse()
    {
        var prompt = Prompt();
        var provider = new ScriptedProvider();
        provider.Enqueue((_, _) => Task.FromResult(ModelProviderResult.Failure(new ModelProviderError(
            ModelProviderErrorKind.RateLimited,
            "rate_limited",
            "Retry.",
            isRetryable: true,
            retryAfter: TimeSpan.Zero))));
        provider.Enqueue(ValidSelection);

        var result = await Orchestrator(provider, maximumAttempts: 2)
            .RerankAsync("request-1", prompt, TimeSpan.FromSeconds(1));

        Assert.Equal(AdvisorRerankStatus.ModelSelected, result.Status);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task NonRetryableFailureImmediatelyUsesFirstValidLocalCandidate()
    {
        var prompt = Prompt();
        var provider = new ScriptedProvider();
        provider.Enqueue((_, _) => Task.FromResult(ModelProviderResult.Failure(new ModelProviderError(
            ModelProviderErrorKind.Unauthorized,
            "unauthorized",
            "No credentials.",
            isRetryable: false))));

        var result = await Orchestrator(provider, maximumAttempts: 3)
            .RerankAsync("request-1", prompt, TimeSpan.FromSeconds(1));

        Assert.Equal(AdvisorRerankStatus.LocalFallback, result.Status);
        Assert.Equal(AdvisorDecisionSource.Local, result.Source);
        Assert.Equal(prompt.CandidateIds[0], result.SelectedCandidateId);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(ModelProviderErrorKind.Unauthorized, result.Error!.Kind);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task TimeoutDoesNotWaitForProviderThatIgnoresCancellation()
    {
        var prompt = Prompt();
        var provider = new ScriptedProvider();
        provider.Enqueue((_, _) => new TaskCompletionSource<ModelProviderResult>().Task);
        var stopwatch = Stopwatch.StartNew();

        var result = await Orchestrator(provider, maximumAttempts: 1)
            .RerankAsync("request-1", prompt, TimeSpan.FromMilliseconds(30));
        stopwatch.Stop();

        Assert.Equal(AdvisorRerankStatus.LocalFallback, result.Status);
        Assert.Equal(ModelProviderErrorKind.Timeout, result.Error!.Kind);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task ExternalCancellationPropagatesInsteadOfPublishingFallback()
    {
        var prompt = Prompt();
        var provider = new ScriptedProvider();
        provider.Enqueue(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Orchestrator(provider, maximumAttempts: 1).RerankAsync(
                "request-1",
                prompt,
                TimeSpan.FromSeconds(2),
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task StaleStateSkipsProviderAndReturnsNoCandidate()
    {
        var prompt = Prompt();
        var provider = new ScriptedProvider();

        var result = await Orchestrator(provider).RerankAsync(
            "request-1",
            prompt,
            TimeSpan.FromSeconds(1),
            isStateCurrent: _ => false);

        Assert.Equal(AdvisorRerankStatus.Stale, result.Status);
        Assert.Null(result.SelectedCandidateId);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task MalformedResponsesRetryThenUseLocalFallback()
    {
        var prompt = Prompt();
        var provider = new ScriptedProvider();
        provider.Enqueue(MalformedResponse);
        provider.Enqueue(MalformedResponse);

        var result = await Orchestrator(provider, maximumAttempts: 2)
            .RerankAsync("request-1", prompt, TimeSpan.FromSeconds(1));

        Assert.Equal(AdvisorRerankStatus.LocalFallback, result.Status);
        Assert.Equal(ModelProviderErrorKind.MalformedResponse, result.Error!.Kind);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task InvalidModelCandidateFallsBackToNextStillValidLocalCandidate()
    {
        var prompt = Prompt();
        var provider = new ScriptedProvider();
        provider.Enqueue(ValidSelection);

        var result = await Orchestrator(provider, maximumAttempts: 2).RerankAsync(
            "request-1",
            prompt,
            TimeSpan.FromSeconds(1),
            isCandidateStillValid: candidateId => candidateId != prompt.CandidateIds[0]);

        Assert.Equal(AdvisorRerankStatus.LocalFallback, result.Status);
        Assert.Equal(prompt.CandidateIds[1], result.SelectedCandidateId);
        Assert.Equal("candidate_no_longer_valid", result.Error!.Code);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public void MemoryCacheExpiresEntriesAndEnforcesCapacity()
    {
        var now = DateTimeOffset.Parse("2026-07-22T00:00:00Z");
        var cache = new MemoryAdvisorSelectionCache(1, () => now);
        var selection = new AdvisorSelection("route-1", null, 0.5, "summary", "risk");
        cache.Set("first", selection, TimeSpan.FromMinutes(1));
        cache.Set("second", selection, TimeSpan.FromMinutes(1));

        Assert.False(cache.TryGet("first", out _));
        Assert.True(cache.TryGet("second", out _));
        now = now.AddMinutes(2);
        Assert.False(cache.TryGet("second", out _));
    }

    private static AdvisorModelOrchestrator Orchestrator(
        ScriptedProvider provider,
        int maximumAttempts = 2) => new(
        provider,
        options: new AdvisorModelExecutionOptions(
            maximumAttempts,
            retryBaseDelay: TimeSpan.Zero,
            maximumRetryDelay: TimeSpan.Zero,
            cacheTimeToLive: TimeSpan.FromMinutes(1)));

    private static Task<ModelProviderResult> ValidSelection(
        ModelProviderRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = JObject.Parse(request.Messages[1].Content);
        var candidates = (JArray)payload["candidates"]!;
        var response = new JObject
        {
            ["protocolVersion"] = payload.Value<string>("protocolVersion"),
            ["stateId"] = payload.Value<string>("stateId"),
            ["candidateSetHash"] = payload.Value<string>("candidateSetHash"),
            ["selectedCandidateId"] = candidates[0]!.Value<string>("candidateId"),
            ["alternativeCandidateId"] = candidates[1]!.Value<string>("candidateId"),
            ["confidence"] = 0.8,
            ["summary"] = "Selected after strategic reranking.",
            ["risk"] = "Use the local branch probabilities."
        };
        return Task.FromResult(ModelProviderResult.Success(new ModelProviderResponse(
            response.ToString(Formatting.None),
            ModelFinishReason.Completed,
            TimeSpan.FromMilliseconds(10))));
    }

    private static Task<ModelProviderResult> MalformedResponse(
        ModelProviderRequest request,
        CancellationToken cancellationToken) => Task.FromResult(ModelProviderResult.Success(
        new ModelProviderResponse("not-json", ModelFinishReason.Completed, TimeSpan.Zero)));

    private static CompressedAdvisorPrompt Prompt()
    {
        var friendly = PlayerState.Create(
            new HeroState(100, "FRIENDLY_HERO", 30, 30),
            new HeroPowerState(101, "POWER", 2),
            new ManaState(2, 0, 0, 2, 0, 0));
        var opponent = PlayerState.Create(
            new HeroState(200, "OPPONENT_HERO", 30, 30),
            new HeroPowerState(201, "OPPONENT_POWER", 2),
            new ManaState(0, 0, 0, 0, 0, 0));
        var state = new RuleGameState(1, PlayerSide.Friendly, friendly, opponent);
        var candidates = new[]
        {
            Candidate("route-1", state, new EndTurnAction(PlayerSide.Friendly), 10),
            Candidate("route-2", state, new UseHeroPowerAction(PlayerSide.Friendly), 9)
        };
        return new AdvisorPromptCompressor().Compress(
            "state-cache-test",
            state,
            candidates,
            OpponentBelief.Balanced);
    }

    private static RiskAwareRouteCandidate Candidate(
        string id,
        RuleGameState state,
        RuleAction action,
        double score)
    {
        var route = new SearchRoute(
            state,
            ImmutableArray.Create(action),
            ImmutableArray<RuleEvent>.Empty,
            1,
            score);
        var dimensions = ScoreDimensions.Zero with { Resources = score };
        return new RiskAwareRouteCandidate(
            id,
            route.Actions,
            ImmutableArray.Create(new ScoredRouteOutcome(route, new DetailedStateScore(dimensions, score))),
            dimensions,
            new RouteRiskStatistics(score, score, 0, 0, 1),
            score,
            1,
            route);
    }

    private sealed class ScriptedProvider : IModelProvider
    {
        private readonly Queue<Func<ModelProviderRequest, CancellationToken, Task<ModelProviderResult>>> _responses = new();

        public string ProviderId => "scripted";
        public string ModelId => "scripted-model";
        public ModelProviderCapabilities Capabilities { get; } = new(true, true, true, true);
        public int CallCount { get; private set; }

        public void Enqueue(Func<ModelProviderRequest, CancellationToken, Task<ModelProviderResult>> response) =>
            _responses.Enqueue(response);

        public Task<ModelProviderResult> CompleteAsync(
            ModelProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (_responses.Count == 0)
                throw new InvalidOperationException("No scripted response remains.");
            return _responses.Dequeue()(request, cancellationToken);
        }
    }
}
