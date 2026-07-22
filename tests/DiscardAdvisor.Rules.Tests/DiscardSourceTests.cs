using System;
using System.Collections.Immutable;
using System.Linq;
using DiscardAdvisor.Rules.Model;
using Xunit;

namespace DiscardAdvisor.Rules.Tests;

public sealed class DiscardSourceTests
{
    private readonly DiscardWarlockRuleEngine _engine = new();

    [Fact]
    public void SoulfireBranchesAcrossEveryRemainingHandCard()
    {
        var soulfire = Card(DiscardWarlockCardIds.Soulfire, 10);
        var hand = new[]
        {
            soulfire,
            Card(DiscardWarlockCardIds.BonewebEgg, 11),
            Card(DiscardWarlockCardIds.HandOfGuldan, 12),
            Card(DiscardWarlockCardIds.SoulBarrage, 13)
        };

        var result = _engine.Apply(CreateState(hand: hand), new PlayCardAction(PlayerSide.Friendly, 10, 200));

        Assert.Equal(3, result.Branches.Length);
        Assert.Equal(1d, result.Branches.Sum(branch => branch.Probability), 10);
        Assert.Equal(new[] { 11, 12, 13 }, result.Branches.Select(branch => branch.Events.Last(ruleEvent => ruleEvent.Type == "discard").TargetEntityId!.Value));
        Assert.All(result.Branches, branch => Assert.Equal(26, branch.State.Opponent.Hero.Health));
    }

    [Fact]
    public void WickedWhispersBranchesOnlyAcrossLowestCostTiesThenBuffs()
    {
        var whispers = Card(DiscardWarlockCardIds.WickedWhispers, 10);
        var lowA = Card(DiscardWarlockCardIds.BonewebEgg, 11);
        var lowB = Card(DiscardWarlockCardIds.DisposableAcolytes, 12);
        var high = Card(DiscardWarlockCardIds.HandOfGuldan, 13);
        var minion = new MinionState(20, "BOARD", 1, 2, 2, 2);

        var result = _engine.Apply(
            CreateState(hand: new[] { whispers, lowA, lowB, high }, board: new[] { minion }),
            new PlayCardAction(PlayerSide.Friendly, 10));

        Assert.Equal(2, result.Branches.Length);
        Assert.Equal(new[] { 11, 12 }, result.Branches.Select(branch => branch.Events.First(ruleEvent => ruleEvent.Type == "discard").TargetEntityId!.Value));
        Assert.All(result.Branches, branch =>
        {
            var discardIndex = branch.Events.Select((ruleEvent, index) => (ruleEvent, index))
                .First(value => value.ruleEvent.Type == "discard").index;
            var buffIndex = branch.Events.Select((ruleEvent, index) => (ruleEvent, index))
                .First(value => value.ruleEvent.Type == "board_buff").index;
            Assert.True(discardIndex < buffIndex);
            Assert.Equal((3, 3), (branch.State.Friendly.Board[0].Attack, branch.State.Friendly.Board[0].Health));
        });
    }

    [Fact]
    public void OcularOccultistOccupiesBoardBeforeActualChoiceDiscard()
    {
        var occultist = Card(DiscardWarlockCardIds.OcularOccultist, 10);
        var target = Card(DiscardWarlockCardIds.HandOfGuldan, 11);
        var played = _engine.Apply(CreateState(hand: new[] { occultist, target }), new PlayCardAction(PlayerSide.Friendly, 10));
        var withChoice = played.State with
        {
            PendingChoice = new PendingChoiceState(
                5,
                "HAND_DISCARD",
                DiscardWarlockCardIds.OcularOccultist,
                ImmutableArray.Create(new ChoiceCandidateState(11, target.CardId)),
                10)
        };

        var result = _engine.Apply(withChoice, new SelectChoiceAction(PlayerSide.Friendly, 5, 11));

        Assert.Contains(result.State.Friendly.Board, minion => minion.EntityId == 10);
        Assert.DoesNotContain(result.State.Friendly.Hand, card => card.EntityId == 11);
        Assert.Equal(1, result.State.Friendly.DiscardCount);
    }

    [Fact]
    public void ChamberDiscardsOnlyDisplayedCandidateThenDrawsTwo()
    {
        var selected = Card(DiscardWarlockCardIds.BonewebEgg, 11);
        var other = Card(DiscardWarlockCardIds.HandOfGuldan, 12);
        var deck = new[]
        {
            Card(DiscardWarlockCardIds.Soulfire, 30),
            Card(DiscardWarlockCardIds.WalkingDead, 31)
        };
        var location = new LocationState(50, DiscardWarlockCardIds.ChamberOfViscidus, 1, 2, 0, 2);
        var choice = new PendingChoiceState(
            7,
            "HAND_DISCARD",
            DiscardWarlockCardIds.ChamberOfViscidus,
            ImmutableArray.Create(
                new ChoiceCandidateState(11, selected.CardId),
                new ChoiceCandidateState(12, other.CardId)),
            50);
        var state = CreateState(hand: new[] { selected, other }, deck: deck, locations: new[] { location }) with { PendingChoice = choice };

        var invalid = _engine.Apply(state, new SelectChoiceAction(PlayerSide.Friendly, 7, 99));
        var result = _engine.Apply(state, new SelectChoiceAction(PlayerSide.Friendly, 7, 11));

        Assert.Equal(RuleError.InvalidTarget, invalid.Error);
        Assert.Equal(3, result.State.Friendly.Hand.Length);
        Assert.DoesNotContain(result.State.Friendly.Hand, card => card.EntityId == 11);
        Assert.Equal((1, 2), (result.State.Friendly.Locations[0].Durability, result.State.Friendly.Locations[0].Cooldown));
        Assert.Equal(new[] { "discard", "draw", "draw" }, result.Events.Where(ruleEvent => ruleEvent.Type is "discard" or "draw").Select(ruleEvent => ruleEvent.Type));
    }

    [Fact]
    public void ChronoclawsBranchesAcrossHighestCostTiesAfterHeroAttack()
    {
        var highA = Card(DiscardWarlockCardIds.SoulBarrage, 11);
        var highB = Card(DiscardWarlockCardIds.DukeOfBelow, 12);
        var low = Card(DiscardWarlockCardIds.Soulfire, 13);
        var state = CreateState(hand: new[] { highA, highB, low }, weapon: new WeaponState(40, DiscardWarlockCardIds.Chronoclaws, 4, 3));

        var result = _engine.Apply(state, new AttackAction(PlayerSide.Friendly, 100, 200));

        Assert.Equal(26, result.State.Opponent.Hero.Health);
        Assert.Equal(2, result.Branches.Length);
        Assert.Equal(new[] { 11, 12 }, result.Branches.Select(branch => branch.Events.Last(ruleEvent => ruleEvent.Type == "discard").TargetEntityId!.Value));
        Assert.All(result.Branches, branch => Assert.Equal(26, branch.State.Opponent.Hero.Health));
    }

    [Fact]
    public void PlatysaurDeathDiscardsOnlyItsStillPresentBoundEntity()
    {
        var platysaur = new MinionState(10, DiscardWarlockCardIds.Platysaur, 1, 1, 1, 1);
        var defender = new MinionState(20, "DEFENDER", 1, 1, 1, 1);
        var bound = Card(DiscardWarlockCardIds.HandOfGuldan, 30);
        var state = CreateState(hand: new[] { bound }, board: new[] { platysaur }, opponentBoard: new[] { defender }) with
        {
            PlatysaurBindings = ImmutableDictionary<int, int>.Empty.Add(10, 30)
        };

        var result = _engine.Apply(state, new AttackAction(PlayerSide.Friendly, 10, 20));
        var withoutBoundCard = _engine.Apply(
            state.WithPlayer(PlayerSide.Friendly, state.Friendly with { Hand = ImmutableArray<HandCardState>.Empty }),
            new AttackAction(PlayerSide.Friendly, 10, 20));

        Assert.DoesNotContain(result.State.Friendly.Hand, card => card.EntityId == 30);
        Assert.Contains(result.Events, ruleEvent => ruleEvent.Type == "discard" && ruleEvent.TargetEntityId == 30);
        Assert.DoesNotContain(withoutBoundCard.Events, ruleEvent => ruleEvent.Type == "discard");
    }

    private static HandCardState Card(string cardId, int entityId) => DiscardWarlockCardCatalog.Create(cardId, entityId);

    private static RuleGameState CreateState(
        HandCardState[]? hand = null,
        HandCardState[]? deck = null,
        MinionState[]? board = null,
        MinionState[]? opponentBoard = null,
        LocationState[]? locations = null,
        WeaponState? weapon = null)
    {
        var friendly = PlayerState.Create(
            new HeroState(100, "HERO", 30, 30),
            new HeroPowerState(101, "POWER", 2),
            new ManaState(10, 0, 0, 10, 0, 0),
            hand,
            board,
            locations,
            deck,
            weapon: weapon);
        var opponent = PlayerState.Create(
            new HeroState(200, "OPPONENT_HERO", 30, 30),
            new HeroPowerState(201, "OPPONENT_POWER", 2),
            new ManaState(10, 0, 0, 10, 0, 0),
            board: opponentBoard);
        return new RuleGameState(1, PlayerSide.Friendly, friendly, opponent);
    }
}
