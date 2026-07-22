using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DiscardAdvisor.Domain;
using Xunit;

namespace DiscardAdvisor.Plugin.Tests;

public sealed class DeckSupportGateTests
{
    private static readonly RuntimeCompatibility SupportedRuntime = new(
        TargetRuntimeCompatibility.HearthstoneBuild,
        TargetRuntimeCompatibility.HdtVersion,
        TargetRuntimeCompatibility.CardDefsSha256,
        TargetRuntimeCompatibility.HearthDbSha256);

    [Fact]
    public void StaticTargetMatchesFrozenJsonProfile()
    {
        using var profile = JsonDocument.Parse(File.ReadAllText(Path.Combine("profiles", "wild-discard-warlock.json")));
        var root = profile.RootElement;
        var jsonCards = root.GetProperty("cards")
            .EnumerateArray()
            .Select(card => new DeckCardCount(card.GetProperty("cardId").GetString()!, card.GetProperty("count").GetInt32()))
            .OrderBy(card => card.CardId, System.StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(TargetDeckProfile.ProfileId, root.GetProperty("profileId").GetString());
        Assert.Equal(TargetDeckProfile.GameMode, root.GetProperty("gameMode").GetString());
        Assert.Equal(TargetDeckProfile.PlayerClass, root.GetProperty("playerClass").GetString());
        Assert.Equal(TargetDeckProfile.DeckSize, root.GetProperty("deckSize").GetInt32());
        Assert.Equal(TargetDeckProfile.DeckHash, root.GetProperty("deckHash").GetProperty("value").GetString());
        Assert.Equal(jsonCards, TargetDeckProfile.Cards);
    }

    [Fact]
    public void CanonicalHashIsOrderIndependentAndMatchesProfile()
    {
        var ids = ExpandTargetDeck();
        ids.Reverse();

        Assert.True(DeckFingerprint.TryCreate(ids, out var fingerprint));
        Assert.NotNull(fingerprint);
        Assert.Equal(TargetDeckProfile.DeckSize, fingerprint!.DeckSize);
        Assert.Equal(TargetDeckProfile.DeckHash, fingerprint.Hash);
    }

    [Fact]
    public void EnablesOnlyTheExactDeckAndRuntime()
    {
        var result = new DeckSupportGate().Evaluate(TargetDeckProfile.GameMode, ExpandTargetDeck(), SupportedRuntime);

        Assert.True(result.IsEnabled);
        Assert.Equal(GateStatus.Enabled, result.Status);
        Assert.Equal(TargetDeckProfile.DeckHash, result.ObservedDeckHash);
    }

    [Theory]
    [InlineData("STANDARD")]
    [InlineData("CASUAL_WILD")]
    [InlineData("")]
    public void RejectsEveryOtherMode(string mode)
    {
        var result = new DeckSupportGate().Evaluate(mode, ExpandTargetDeck(), SupportedRuntime);

        Assert.Equal(GateStatus.UnsupportedMode, result.Status);
    }

    [Fact]
    public void RejectsAnIncompleteDeck()
    {
        var cards = ExpandTargetDeck();
        cards.RemoveAt(0);

        var result = new DeckSupportGate().Evaluate(TargetDeckProfile.GameMode, cards, SupportedRuntime);

        Assert.Equal(GateStatus.IncompleteDeck, result.Status);
    }

    [Fact]
    public void RejectsADifferentThirtyCardDeck()
    {
        var cards = ExpandTargetDeck();
        cards[0] = "NOT_TARGET_CARD";

        var result = new DeckSupportGate().Evaluate(TargetDeckProfile.GameMode, cards, SupportedRuntime);

        Assert.Equal(GateStatus.DeckMismatch, result.Status);
        Assert.NotEqual(TargetDeckProfile.DeckHash, result.ObservedDeckHash);
    }

    [Fact]
    public void RejectsUnknownCardIdsInsteadOfGuessing()
    {
        var cards = ExpandTargetDeck().Cast<string?>().ToList();
        cards[0] = null;

        var result = new DeckSupportGate().Evaluate(TargetDeckProfile.GameMode, cards, SupportedRuntime);

        Assert.Equal(GateStatus.IncompleteDeck, result.Status);
    }

    public static IEnumerable<object[]> UnsupportedRuntimeCases()
    {
        yield return new object[]
        {
            new RuntimeCompatibility(1, TargetRuntimeCompatibility.HdtVersion, TargetRuntimeCompatibility.CardDefsSha256, TargetRuntimeCompatibility.HearthDbSha256),
            GateStatus.UnsupportedPatch
        };
        yield return new object[]
        {
            new RuntimeCompatibility(TargetRuntimeCompatibility.HearthstoneBuild, "1.54.0", TargetRuntimeCompatibility.CardDefsSha256, TargetRuntimeCompatibility.HearthDbSha256),
            GateStatus.UnsupportedHdtVersion
        };
        yield return new object[]
        {
            new RuntimeCompatibility(TargetRuntimeCompatibility.HearthstoneBuild, TargetRuntimeCompatibility.HdtVersion, "wrong", TargetRuntimeCompatibility.HearthDbSha256),
            GateStatus.UnsupportedCardDefinitions
        };
        yield return new object[]
        {
            new RuntimeCompatibility(TargetRuntimeCompatibility.HearthstoneBuild, TargetRuntimeCompatibility.HdtVersion, TargetRuntimeCompatibility.CardDefsSha256, "wrong"),
            GateStatus.UnsupportedHearthDb
        };
    }

    [Theory]
    [MemberData(nameof(UnsupportedRuntimeCases))]
    public void RejectsUnsupportedRuntime(RuntimeCompatibility runtime, GateStatus expectedStatus)
    {
        var result = new DeckSupportGate().Evaluate(TargetDeckProfile.GameMode, ExpandTargetDeck(), runtime);

        Assert.Equal(expectedStatus, result.Status);
    }

    private static List<string> ExpandTargetDeck() => TargetDeckProfile.Cards
        .SelectMany(card => Enumerable.Repeat(card.CardId, card.Count))
        .ToList();
}
