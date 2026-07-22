using System;
using System.Linq;
using DiscardAdvisor.Rules;
using DiscardAdvisor.Rules.Model;
using DiscardAdvisor.Search;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DiscardAdvisor.Llm.Tests;

public sealed class AdvisorProtocolTests
{
    [Fact]
    public void CompressorProducesDeterministicBoundedTopKPayload()
    {
        var (state, candidates) = SearchCandidates();
        var compressor = new AdvisorPromptCompressor();

        var first = compressor.Compress("state-1", state, candidates, OpponentBelief.Balanced, 2);
        var second = compressor.Compress("state-1", state, candidates, OpponentBelief.Balanced, 2);
        var document = JObject.Parse(first.PayloadJson);

        Assert.Equal(first.PayloadJson, second.PayloadJson);
        Assert.Equal(first.CandidateSetHash, second.CandidateSetHash);
        Assert.Equal(64, first.CandidateSetHash.Length);
        Assert.Equal(2, first.CandidateIds.Length);
        Assert.Equal(2, document["candidates"]!.Count());
        Assert.Equal(first.CandidateSetHash, document.Value<string>("candidateSetHash"));
        Assert.Null(document.SelectToken("$.candidates[0].outcomes"));
        Assert.InRange(first.PayloadJson.Length, 1, 8192);
        Assert.NotNull(document.SelectToken("$.candidates[0].steps[0].sourceCardId"));
    }

    [Fact]
    public void CandidateHashChangesWhenRankedSetChanges()
    {
        var (state, candidates) = SearchCandidates();
        var compressor = new AdvisorPromptCompressor();

        var original = compressor.Compress("state-1", state, candidates, OpponentBelief.Balanced, 2);
        var reversed = compressor.Compress("state-1", state, candidates.Reverse(), OpponentBelief.Balanced, 2);

        Assert.NotEqual(original.CandidateSetHash, reversed.CandidateSetHash);
    }

    [Fact]
    public void ProviderRequestCarriesStructuredSchemaAndOnlyCompressedPayload()
    {
        var (state, candidates) = SearchCandidates();
        var compressor = new AdvisorPromptCompressor();
        var prompt = compressor.Compress("state-1", state, candidates, OpponentBelief.Balanced, 3);

        var request = compressor.CreateProviderRequest(
            "request-1",
            prompt,
            TimeSpan.FromSeconds(2));

        Assert.Equal(2, request.Messages.Length);
        Assert.Equal(prompt.PayloadJson, request.Messages[1].Content);
        Assert.True(request.ResponseContract.Strict);
        Assert.NotNull(JObject.Parse(request.ResponseContract.JsonSchema));
        Assert.Contains("Do not invent actions", request.Messages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatorAcceptsSelectionFromCurrentCandidateSet()
    {
        var prompt = Prompt();
        var selected = prompt.CandidateIds[0];
        var alternative = prompt.CandidateIds[1];
        var json = ResponseJson(prompt, selected, alternative);

        var result = AdvisorSelectionProtocol.Validate(
            json,
            new AdvisorProtocolContext(prompt.StateId, prompt.CandidateSetHash, prompt.CandidateIds));

        Assert.True(result.IsValid);
        Assert.Equal(selected, result.Selection!.SelectedCandidateId);
        Assert.Equal(alternative, result.Selection.AlternativeCandidateId);
        Assert.Equal(0.76, result.Selection.Confidence, 10);
    }

    [Theory]
    [InlineData("stateId", "stale-state", "stale_state")]
    [InlineData("candidateSetHash", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "candidate_set_mismatch")]
    [InlineData("selectedCandidateId", "route-not-supplied", "unknown_candidate")]
    public void ValidatorRejectsStaleOrInventedReferences(string property, string value, string expectedCode)
    {
        var prompt = Prompt();
        var document = JObject.Parse(ResponseJson(prompt, prompt.CandidateIds[0], null));
        document[property] = value;

        var result = AdvisorSelectionProtocol.Validate(
            document.ToString(Newtonsoft.Json.Formatting.None),
            new AdvisorProtocolContext(prompt.StateId, prompt.CandidateSetHash, prompt.CandidateIds));

        Assert.False(result.IsValid);
        Assert.Equal(expectedCode, Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void ValidatorRejectsExtraDuplicateAndNoLongerValidCandidates()
    {
        var prompt = Prompt();
        var valid = ResponseJson(prompt, prompt.CandidateIds[0], null);
        var extra = JObject.Parse(valid);
        extra["targetEntityId"] = 999;
        var duplicate = valid.Replace(
            "\"stateId\":\"state-1\"",
            "\"stateId\":\"state-1\",\"stateId\":\"state-1\"");

        var extraResult = AdvisorSelectionProtocol.Validate(
            extra.ToString(Newtonsoft.Json.Formatting.None),
            new AdvisorProtocolContext(prompt.StateId, prompt.CandidateSetHash, prompt.CandidateIds));
        var duplicateResult = AdvisorSelectionProtocol.Validate(
            duplicate,
            new AdvisorProtocolContext(prompt.StateId, prompt.CandidateSetHash, prompt.CandidateIds));
        var invalidatedResult = AdvisorSelectionProtocol.Validate(
            valid,
            new AdvisorProtocolContext(
                prompt.StateId,
                prompt.CandidateSetHash,
                prompt.CandidateIds,
                _ => false));

        Assert.Equal("unknown_property", Assert.Single(extraResult.Errors).Code);
        Assert.Equal("invalid_json", Assert.Single(duplicateResult.Errors).Code);
        Assert.Equal("candidate_no_longer_valid", Assert.Single(invalidatedResult.Errors).Code);
    }

    private static CompressedAdvisorPrompt Prompt()
    {
        var (state, candidates) = SearchCandidates();
        return new AdvisorPromptCompressor().Compress(
            "state-1",
            state,
            candidates,
            OpponentBelief.Balanced,
            3);
    }

    private static string ResponseJson(
        CompressedAdvisorPrompt prompt,
        string selected,
        string? alternative)
    {
        var response = new JObject
        {
            ["protocolVersion"] = AdvisorPromptCompressor.ProtocolVersion,
            ["stateId"] = prompt.StateId,
            ["candidateSetHash"] = prompt.CandidateSetHash,
            ["selectedCandidateId"] = selected,
            ["confidence"] = 0.76,
            ["summary"] = "Use the selected local route.",
            ["risk"] = "Random branches remain possible."
        };
        if (alternative is not null)
            response["alternativeCandidateId"] = alternative;
        return response.ToString(Newtonsoft.Json.Formatting.None);
    }

    private static (RuleGameState State, RiskAwareRouteCandidate[] Candidates) SearchCandidates()
    {
        var hand = new[]
        {
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.Soulfire, 10),
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.EntropicContinuity, 11),
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.DisposableAcolytes, 12)
        };
        var friendly = PlayerState.Create(
            new HeroState(100, "FRIENDLY_HERO", 25, 30),
            new HeroPowerState(101, "WARLOCK_POWER", 2),
            new ManaState(10, 0, 0, 10, 0, 0),
            hand,
            new[] { new MinionState(20, "FRIENDLY_MINION", 1, 2, 3, 3) });
        var opponent = PlayerState.Create(
            new HeroState(200, "OPPONENT_HERO", 20, 30),
            new HeroPowerState(201, "OPPONENT_POWER", 2),
            new ManaState(0, 0, 0, 10, 0, 0),
            board: new[] { new MinionState(30, "OPPONENT_MINION", 1, 2, 2, 2, Taunt: true) });
        var state = new RuleGameState(6, PlayerSide.Friendly, friendly, opponent, NextEntityId: 1000);
        var result = new BeamSearch().Search(
            state,
            new BeamSearchOptions(
                BeamWidth: 64,
                MaximumActions: 2,
                TopK: 5,
                TimeBudget: TimeSpan.FromSeconds(2)));
        return (state, result.Candidates.ToArray());
    }
}
