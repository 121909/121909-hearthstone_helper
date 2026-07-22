using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace DiscardAdvisor.Llm.Tests;

public sealed class ModelProviderContractsTests
{
    [Fact]
    public void RequestFreezesMessagesAndKeepsStructuredContract()
    {
        var messages = new List<ModelMessage>
        {
            new(ModelMessageRole.System, "Return only the selected candidate."),
            new(ModelMessageRole.User, "state-1")
        };
        var contract = new ModelResponseContract("advisor_result", "{\"type\":\"object\"}");

        var request = new ModelProviderRequest(
            "request-1",
            messages,
            contract,
            TimeSpan.FromSeconds(2),
            maxOutputTokens: 256,
            temperature: 0.1);
        messages.Clear();

        Assert.Equal(2, request.Messages.Length);
        Assert.Same(contract, request.ResponseContract);
        Assert.Equal(256, request.MaxOutputTokens);
        Assert.Equal(0.1, request.Temperature, 10);
    }

    [Fact]
    public void RequestRejectsInvalidPortableSettings()
    {
        var contract = new ModelResponseContract("result", "{}");
        var messages = new[] { new ModelMessage(ModelMessageRole.User, "input") };

        Assert.Throws<ArgumentException>(() => new ModelProviderRequest(
            string.Empty,
            messages,
            contract,
            TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModelProviderRequest(
            "id",
            messages,
            contract,
            TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModelProviderRequest(
            "id",
            messages,
            contract,
            TimeSpan.FromSeconds(1),
            temperature: 2.1));
    }

    [Fact]
    public void ResultRepresentsExactlyOneSuccessOrFailure()
    {
        var response = new ModelProviderResponse(
            "{}",
            ModelFinishReason.Completed,
            TimeSpan.FromMilliseconds(25),
            "provider-request",
            new ModelTokenUsage(10, 4));
        var error = new ModelProviderError(
            ModelProviderErrorKind.RateLimited,
            "rate_limited",
            "Try again later.",
            true,
            TimeSpan.FromSeconds(1));

        var success = ModelProviderResult.Success(response);
        var failure = ModelProviderResult.Failure(error);

        Assert.True(success.IsSuccess);
        Assert.Same(response, success.Response);
        Assert.Null(success.Error);
        Assert.False(failure.IsSuccess);
        Assert.Null(failure.Response);
        Assert.Same(error, failure.Error);
        Assert.Equal(14, response.TokenUsage!.TotalTokens);
    }

    [Fact]
    public async Task ProviderBoundarySupportsSuccessAndCancellation()
    {
        var provider = new FakeModelProvider();
        var request = new ModelProviderRequest(
            "request-1",
            new[] { new ModelMessage(ModelMessageRole.User, "input") },
            new ModelResponseContract("result", "{}"),
            TimeSpan.FromSeconds(1));

        var result = await provider.CompleteAsync(request);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.True(result.IsSuccess);
        Assert.Equal("fake", provider.ProviderId);
        Assert.Equal("fake-model", provider.ModelId);
        Assert.True(provider.Capabilities.SupportsStructuredOutput);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.CompleteAsync(request, cancellation.Token));
    }

    private sealed class FakeModelProvider : IModelProvider
    {
        public string ProviderId => "fake";

        public string ModelId => "fake-model";

        public ModelProviderCapabilities Capabilities { get; } = new(true, true, true, true);

        public async Task<ModelProviderResult> CompleteAsync(
            ModelProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return ModelProviderResult.Success(new ModelProviderResponse(
                "{}",
                ModelFinishReason.Completed,
                TimeSpan.Zero,
                tokenUsage: new ModelTokenUsage(1, 1)));
        }
    }
}
