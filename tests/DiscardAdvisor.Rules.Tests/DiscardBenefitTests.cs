using System;
using System.Collections.Immutable;
using System.Linq;
using DiscardAdvisor.Rules.Model;
using Xunit;

namespace DiscardAdvisor.Rules.Tests;

public sealed class DiscardBenefitTests
{
    private readonly DiscardWarlockRuleEngine _engine = new();

    [Fact]
    public void WickedWhispersSummonsEggSpidersBeforeBuffingThem()
    {
        var whispers = Card(DiscardWarlockCardIds.WickedWhispers, 10);
        var egg = Card(DiscardWarlockCardIds.BonewebEgg, 11);
        var result = _engine.Apply(
            CreateState(hand: new[] { whispers, egg }),
            new PlayCardAction(PlayerSide.Friendly, 10));

        var spiders = result.State.Friendly.Board.Where(minion => minion.CardId == DiscardWarlockCardIds.BonewebSpider).ToArray();
        Assert.Equal(2, spiders.Length);
        Assert.All(spiders, spider => Assert.Equal((3, 2), (spider.Attack, spider.Health)));
        var discardIndex = EventIndex(result.Events, "discard");
        var summonIndex = EventIndex(result.Events, "summon");
        var buffIndex = EventIndex(result.Events, "board_buff");
        Assert.True(discardIndex < summonIndex && summonIndex < buffIndex);
    }

    [Fact]
    public void EggSummonsOnlyOneSpiderWhenOneBoardSlotRemains()
    {
        var whispers = Card(DiscardWarlockCardIds.WickedWhispers, 10);
        var egg = Card(DiscardWarlockCardIds.BonewebEgg, 11);
        var board = Enumerable.Range(0, 6).Select(index => Minion(20 + index, index + 1)).ToArray();

        var result = _engine.Apply(
            CreateState(hand: new[] { whispers, egg }, board: board),
            new PlayCardAction(PlayerSide.Friendly, 10));

        Assert.Equal(7, result.State.Friendly.BoardCount);
        Assert.Single(result.State.Friendly.Board.Where(minion => minion.CardId == DiscardWarlockCardIds.BonewebSpider));
        Assert.Contains(result.Events, ruleEvent => ruleEvent.Type == "summon_failed_board_full");
    }

    [Fact]
    public void OcularOccupiesSeventhSlotBeforeDiscardedWalkingDeadTriesToSummon()
    {
        var occultist = Card(DiscardWarlockCardIds.OcularOccultist, 10);
        var walkingDead = Card(DiscardWarlockCardIds.WalkingDead, 11);
        var board = Enumerable.Range(0, 6).Select(index => Minion(20 + index, index + 1)).ToArray();
        var played = _engine.Apply(
            CreateState(hand: new[] { occultist, walkingDead }, board: board),
            new PlayCardAction(PlayerSide.Friendly, 10));
        var choice = new PendingChoiceState(
            5,
            "HAND_DISCARD",
            DiscardWarlockCardIds.OcularOccultist,
            ImmutableArray.Create(new ChoiceCandidateState(11, walkingDead.CardId)),
            10);

        var result = _engine.Apply(played.State with { PendingChoice = choice }, new SelectChoiceAction(PlayerSide.Friendly, 5, 11));

        Assert.Equal(7, result.State.Friendly.BoardCount);
        Assert.DoesNotContain(result.State.Friendly.Board, minion => minion.EntityId == 11);
        Assert.Contains(result.State.Friendly.Graveyard, card => card.EntityId == 11);
        Assert.Contains(result.Events, ruleEvent => ruleEvent.Type == "summon_failed_board_full");
    }

    [Fact]
    public void DiscardedSilverwareGolemMovesFromGraveyardToBoard()
    {
        var occultist = Card(DiscardWarlockCardIds.OcularOccultist, 10);
        var golem = Card(DiscardWarlockCardIds.SilverwareGolem, 11);
        var played = _engine.Apply(CreateState(hand: new[] { occultist, golem }), new PlayCardAction(PlayerSide.Friendly, 10));
        var choice = new PendingChoiceState(
            5,
            "HAND_DISCARD",
            DiscardWarlockCardIds.OcularOccultist,
            ImmutableArray.Create(new ChoiceCandidateState(11, golem.CardId)),
            10);

        var result = _engine.Apply(played.State with { PendingChoice = choice }, new SelectChoiceAction(PlayerSide.Friendly, 5, 11));

        Assert.Contains(result.State.Friendly.Board, minion => minion.EntityId == 11 && minion.CardId == golem.CardId);
        Assert.DoesNotContain(result.State.Friendly.Graveyard, card => card.EntityId == 11);
    }

    [Fact]
    public void HandOfGuldanDiscardDrawsThreeAfterSourceEffect()
    {
        var soulfire = Card(DiscardWarlockCardIds.Soulfire, 10);
        var handOfGuldan = Card(DiscardWarlockCardIds.HandOfGuldan, 11);
        var deck = new[]
        {
            Card(DiscardWarlockCardIds.BonewebEgg, 30),
            Card(DiscardWarlockCardIds.WalkingDead, 31),
            Card(DiscardWarlockCardIds.DukeOfBelow, 32)
        };

        var result = _engine.Apply(
            CreateState(hand: new[] { soulfire, handOfGuldan }, deck: deck),
            new PlayCardAction(PlayerSide.Friendly, 10, 200));

        Assert.Equal(3, result.State.Friendly.Hand.Length);
        Assert.Empty(result.State.Friendly.Deck);
        Assert.True(EventIndex(result.Events, "damage") < EventIndex(result.Events, "discard"));
        Assert.True(EventIndex(result.Events, "discard") < EventIndex(result.Events, "draw"));
    }

    [Fact]
    public void EveryDiscardGrowsDukeInHandAndOnBoard()
    {
        var occultist = Card(DiscardWarlockCardIds.OcularOccultist, 10);
        var target = Card(DiscardWarlockCardIds.BonewebEgg, 11);
        var dukeInHand = Card(DiscardWarlockCardIds.DukeOfBelow, 12);
        var dukeOnBoard = new MinionState(20, DiscardWarlockCardIds.DukeOfBelow, 1, 2, 2, 2);
        var played = _engine.Apply(
            CreateState(hand: new[] { occultist, target, dukeInHand }, board: new[] { dukeOnBoard }),
            new PlayCardAction(PlayerSide.Friendly, 10));
        var choice = new PendingChoiceState(
            5,
            "HAND_DISCARD",
            DiscardWarlockCardIds.OcularOccultist,
            ImmutableArray.Create(new ChoiceCandidateState(11, target.CardId)),
            10);

        var result = _engine.Apply(played.State with { PendingChoice = choice }, new SelectChoiceAction(PlayerSide.Friendly, 5, 11));

        var handDuke = result.State.Friendly.Hand.Single(card => card.EntityId == 12);
        var boardDuke = result.State.Friendly.Board.Single(minion => minion.EntityId == 20);
        Assert.Equal((4, 4), (handDuke.Attack, handDuke.Health));
        Assert.Equal((4, 4, 4), (boardDuke.Attack, boardDuke.Health, boardDuke.MaxHealth));
        Assert.Equal(1, result.State.Friendly.DiscardCount);
    }

    [Fact]
    public void TemporaryHandOfGuldanExpiresAndTriggersDrawBeforeTurnEnds()
    {
        var temporaryHand = Card(DiscardWarlockCardIds.HandOfGuldan, 11) with { Temporary = true };
        var deck = new[]
        {
            Card(DiscardWarlockCardIds.BonewebEgg, 30),
            Card(DiscardWarlockCardIds.WalkingDead, 31),
            Card(DiscardWarlockCardIds.DukeOfBelow, 32)
        };

        var result = _engine.Apply(CreateState(hand: new[] { temporaryHand }, deck: deck), new EndTurnAction(PlayerSide.Friendly));

        Assert.Equal(PlayerSide.Opponent, result.State.ActiveSide);
        Assert.Equal(3, result.State.Friendly.Hand.Length);
        Assert.All(result.State.Friendly.Hand, card => Assert.False(card.Temporary));
        Assert.Equal(1, result.State.Friendly.DiscardCount);
        Assert.True(EventIndex(result.Events, "discard") < EventIndex(result.Events, "end_turn"));
    }

    [Fact]
    public void RandomBenefitsRecordOnlyAvailableBoardSlotsAndMissileCount()
    {
        var acolytes = Card(DiscardWarlockCardIds.DisposableAcolytes, 10);
        var barrage = Card(DiscardWarlockCardIds.SoulBarrage, 11);
        var board = Enumerable.Range(0, 6).Select(index => Minion(20 + index, index + 1)).ToArray();
        var acolyteResult = _engine.Apply(
            CreateState(hand: new[] { acolytes }, board: board),
            new PlayCardAction(PlayerSide.Friendly, 10));
        var barrageResult = _engine.Apply(CreateState(hand: new[] { barrage }), new PlayCardAction(PlayerSide.Friendly, 11));

        Assert.Equal(1, acolyteResult.Events.Last(ruleEvent => ruleEvent.Type == "random_one_cost_summon_pending").Amount);
        Assert.Equal(5, barrageResult.Events.Last(ruleEvent => ruleEvent.Type == "random_damage_pending").Amount);
    }

    private static int EventIndex(ImmutableArray<RuleEvent> events, string type) => events
        .Select((ruleEvent, index) => (ruleEvent, index))
        .First(value => value.ruleEvent.Type == type).index;

    private static HandCardState Card(string cardId, int entityId) => DiscardWarlockCardCatalog.Create(cardId, entityId);

    private static MinionState Minion(int entityId, int position) => new(entityId, $"M{entityId}", position, 1, 1, 1);

    private static RuleGameState CreateState(
        HandCardState[]? hand = null,
        HandCardState[]? deck = null,
        MinionState[]? board = null)
    {
        var friendly = PlayerState.Create(
            new HeroState(100, "HERO", 30, 30),
            new HeroPowerState(101, "POWER", 2),
            new ManaState(10, 0, 0, 10, 0, 0),
            hand,
            board,
            deck: deck);
        var opponent = PlayerState.Create(
            new HeroState(200, "OPPONENT_HERO", 30, 30),
            new HeroPowerState(201, "OPPONENT_POWER", 2),
            new ManaState(10, 0, 0, 10, 0, 0));
        return new RuleGameState(1, PlayerSide.Friendly, friendly, opponent);
    }
}
