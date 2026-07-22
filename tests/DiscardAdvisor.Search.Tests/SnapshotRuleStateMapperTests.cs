using System;
using System.Linq;
using DiscardAdvisor.Domain.Snapshots;
using DiscardAdvisor.Rules;
using Xunit;

namespace DiscardAdvisor.Search.Tests;

public sealed class SnapshotRuleStateMapperTests
{
    [Fact]
    public void MapsVisibleAndDerivedSnapshotState()
    {
        var snapshot = CreateSnapshot(DiscardWarlockCardIds.Soulfire);

        var result = new SnapshotRuleStateMapper().Map(snapshot);

        Assert.True(result.IsSupported);
        Assert.NotNull(result.State);
        var state = result.State!;
        Assert.True(state.Friendly.Hand[0].Temporary);
        Assert.Equal(3, state.Friendly.DiscardCount);
        Assert.Equal(30, state.Bindings[20]);
        Assert.Equal(2, state.Friendly.Deck.Count(card => card.CardId == DiscardWarlockCardIds.ShredOfTime));
        Assert.False(state.Friendly.DeckOrderKnown);
        Assert.Equal((2, 0), (state.Friendly.Locations[0].Durability, state.Friendly.Locations[0].Cooldown));
        Assert.True(state.Friendly.Board[0].DivineShield);
        Assert.True(state.Friendly.Board[0].Poisonous);
        Assert.True(state.Friendly.Board[0].Lifesteal);
        Assert.NotNull(state.PendingChoice);
        Assert.Equal(10, state.PendingChoice!.Candidates[0].EntityId);
    }

    [Fact]
    public void RejectsUnknownFriendlyHandCardWithoutGuessing()
    {
        var result = new SnapshotRuleStateMapper().Map(CreateSnapshot("UNKNOWN_CARD"));

        Assert.False(result.IsSupported);
        Assert.Null(result.State);
        Assert.Contains("unknown_hand_card:UNKNOWN_CARD", result.UnsupportedInteractions);
    }

    [Fact]
    public void RejectsIncompleteKnownDeckWithoutInventingDrawOrder()
    {
        var snapshot = CreateSnapshot(DiscardWarlockCardIds.Soulfire);
        var friendly = new FriendlyPlayerSnapshot(
            snapshot.Friendly.Hero,
            snapshot.Friendly.HeroPower,
            snapshot.Friendly.Mana,
            snapshot.Friendly.Hand,
            snapshot.Friendly.Board,
            snapshot.Friendly.Locations,
            snapshot.Friendly.OriginalDeck,
            Array.Empty<DeckEntrySnapshot>(),
            5,
            snapshot.Friendly.Fatigue,
            snapshot.Friendly.Graveyard,
            snapshot.Friendly.Discarded,
            snapshot.Friendly.DiscardCount,
            snapshot.Friendly.Weapon);
        snapshot = new GameSnapshot(
            snapshot.RuleSetVersion,
            snapshot.HearthstoneBuild,
            snapshot.HdtVersion,
            snapshot.CardDefsHash,
            snapshot.GameId,
            snapshot.StateId,
            snapshot.TurnNumber,
            snapshot.Step,
            snapshot.ActivePlayer,
            snapshot.RemainingTurnTimeMs,
            snapshot.IsStable,
            friendly,
            snapshot.Opponent,
            snapshot.ActionsThisTurn,
            snapshot.Derived,
            snapshot.CurrentChoice);

        var result = new SnapshotRuleStateMapper().Map(snapshot);

        Assert.False(result.IsSupported);
        Assert.Contains("incomplete_known_deck:2/5", result.UnsupportedInteractions);
    }

    [Fact]
    public void RejectsRebornWithoutGuessingUnbuffedResurrectionStats()
    {
        var result = new SnapshotRuleStateMapper().Map(CreateSnapshot(DiscardWarlockCardIds.Soulfire, reborn: true));

        Assert.False(result.IsSupported);
        Assert.Contains(
            $"unsupported_reborn:20:{DiscardWarlockCardIds.OcularOccultist}",
            result.UnsupportedInteractions);
    }

    [Fact]
    public void RejectsInteractionsAlreadyMarkedUnsupportedBySnapshotCapture()
    {
        var result = new SnapshotRuleStateMapper().Map(CreateSnapshot(
            DiscardWarlockCardIds.Soulfire,
            unsupportedInteractions: new[] { "unknown_visible_enchantment:77" }));

        Assert.False(result.IsSupported);
        Assert.Contains("unknown_visible_enchantment:77", result.UnsupportedInteractions);
    }

    private static GameSnapshot CreateSnapshot(
        string handCardId,
        bool reborn = false,
        string[]? unsupportedInteractions = null)
    {
        var friendly = new FriendlyPlayerSnapshot(
            new HeroSnapshot(1, "HERO_07", 30, 30, 0, 0, false, false, 0, 1),
            new HeroPowerSnapshot(2, "CS2_056", 2, true, 0, 1),
            new ManaSnapshot(5, 0, 0, 5, 0, 0),
            new[] { new HandCardSnapshot(10, handCardId, 1, 1, true) },
            new[] { new MinionSnapshot(20, DiscardWarlockCardIds.OcularOccultist, 1, 3, 6, 6, 0, 1, false, false, true, false, false, false, true, true, true, reborn, false, false, false, true) },
            new[] { new LocationSnapshot(40, DiscardWarlockCardIds.ChamberOfViscidus, 2, 2, 0, true) },
            new[] { new DeckEntrySnapshot(DiscardWarlockCardIds.Soulfire, 1) },
            new[] { new DeckEntrySnapshot(DiscardWarlockCardIds.ShredOfTime, 1) },
            2,
            0,
            new[] { new ZoneCardSnapshot(50, DiscardWarlockCardIds.CursedCatacombs) },
            Array.Empty<ZoneCardSnapshot>(),
            3);
        var opponent = new OpponentPlayerSnapshot(
            new HeroSnapshot(3, "HERO_08", 30, 30, 0, 0, false, false, 0, 1),
            new HeroPowerSnapshot(4, "CS2_034", 2, true, 0, 1),
            3,
            Array.Empty<MinionSnapshot>(),
            Array.Empty<LocationSnapshot>(),
            20,
            0,
            Array.Empty<ZoneCardSnapshot>(),
            Array.Empty<DeckEntrySnapshot>(),
            0,
            Array.Empty<string>());
        return new GameSnapshot(
            "0.3.0",
            246003,
            "1.53.11",
            new string('a', 64),
            Guid.NewGuid(),
            "turn-3:test",
            3,
            "MAIN_ACTION",
            "FRIENDLY",
            60000,
            true,
            friendly,
            opponent,
            Array.Empty<SnapshotAction>(),
            new DerivedStateSnapshot(
                new[] { new PlatysaurBindingSnapshot(20, 30) },
                new[] { 10 },
                2,
                unsupportedInteractions ?? Array.Empty<string>()),
            new ChoiceSnapshot(
                7,
                "HAND_DISCARD",
                new[] { new EntityReferenceSnapshot(10, handCardId) },
                20));
    }
}
