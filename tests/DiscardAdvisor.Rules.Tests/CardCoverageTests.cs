using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using DiscardAdvisor.Rules.Model;
using Xunit;

namespace DiscardAdvisor.Rules.Tests;

public sealed class CardCoverageTests
{
    private readonly DiscardWarlockRuleEngine _engine = new();

    public static TheoryData<string> TargetCards
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var cardId in DiscardWarlockCardCatalog.TargetCardIds)
                data.Add(cardId);
            return data;
        }
    }

    public static TheoryData<string> CostedTargetCards
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var cardId in DiscardWarlockCardCatalog.TargetCardIds.Where(cardId =>
                         DiscardWarlockCardCatalog.Create(cardId, 1).Cost > 0))
                data.Add(cardId);
            return data;
        }
    }

    public static TheoryData<string> BoardPermanents
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var cardId in DiscardWarlockCardCatalog.TargetCardIds.Where(cardId =>
                         DiscardWarlockCardCatalog.Create(cardId, 1).CardType is RuleCardType.Minion or RuleCardType.Location))
                data.Add(cardId);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(TargetCards))]
    public void EveryCardHasAResolvableDiscardPath(string cardId)
    {
        var occultist = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.OcularOccultist, 10);
        var target = DiscardWarlockCardCatalog.Create(cardId, 11);
        var played = _engine.Apply(CreateState(new[] { occultist, target }), new PlayCardAction(PlayerSide.Friendly, 10));
        var withChoice = played.State with
        {
            PendingChoice = new PendingChoiceState(
                5,
                "HAND_DISCARD",
                DiscardWarlockCardIds.OcularOccultist,
                ImmutableArray.Create(new ChoiceCandidateState(11, cardId)),
                10)
        };

        var result = _engine.Apply(withChoice, new SelectChoiceAction(PlayerSide.Friendly, 5, 11));

        Assert.True(result.IsLegal);
        Assert.Contains(result.Events, ruleEvent =>
            ruleEvent.Type == "discard" && ruleEvent.TargetEntityId == 11 && ruleEvent.CardId == cardId);
        AssertValidOutcomes(result);
    }

    [Theory]
    [MemberData(nameof(CostedTargetCards))]
    public void EveryCostedCardRejectsInsufficientManaWithoutMutation(string cardId)
    {
        var card = DiscardWarlockCardCatalog.Create(cardId, 10);
        var state = CreateState(new[] { card }, mana: 0);
        var target = card.TargetKind == TargetKind.None ? null : (int?)200;

        var result = _engine.Apply(state, new PlayCardAction(PlayerSide.Friendly, 10, target));

        Assert.False(result.IsLegal);
        Assert.Equal(RuleError.InsufficientMana, result.Error);
        Assert.Same(state, result.State);
    }

    [Theory]
    [MemberData(nameof(BoardPermanents))]
    public void EveryBoardPermanentRejectsAFullBoard(string cardId)
    {
        var card = DiscardWarlockCardCatalog.Create(cardId, 10);
        var board = Enumerable.Range(0, 7)
            .Select(index => new MinionState(20 + index, $"M{index}", index + 1, 1, 1, 1));
        var state = CreateState(new[] { card }, board: board);

        var result = _engine.Apply(state, new PlayCardAction(PlayerSide.Friendly, 10));

        Assert.False(result.IsLegal);
        Assert.Equal(RuleError.BoardFull, result.Error);
    }

    [Theory]
    [MemberData(nameof(TargetCards))]
    public void EveryCardPlayProducesInvariantValidOutcomes(string cardId)
    {
        var card = DiscardWarlockCardCatalog.Create(cardId, 10);
        var state = CreateState(new[] { card });
        var target = card.TargetKind == TargetKind.None ? null : (int?)200;

        var result = _engine.Apply(state, new PlayCardAction(PlayerSide.Friendly, 10, target));

        Assert.True(result.IsLegal);
        AssertValidOutcomes(result);
    }

    [Fact]
    public void RepeatedDiscardsAccumulateDukeGrowthDeterministically()
    {
        var firstOccultist = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.OcularOccultist, 10);
        var secondOccultist = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.OcularOccultist, 11);
        var firstTarget = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.CursedCatacombs, 12);
        var secondTarget = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.Soulfire, 13);
        var duke = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.DukeOfBelow, 14);
        var state = CreateState(new[] { firstOccultist, secondOccultist, firstTarget, secondTarget, duke });

        state = PlayAndChooseDiscard(state, 10, 12, 1);
        state = PlayAndChooseDiscard(state, 11, 13, 2);

        var grownDuke = state.Friendly.Hand.Single(card => card.EntityId == 14);
        Assert.Equal((6, 6), (grownDuke.Attack, grownDuke.Health));
        Assert.Equal(2, state.Friendly.DiscardCount);
    }

    [Fact]
    public void HandOfGuldanChainCastsShredAndContinuesRemainingDraws()
    {
        var soulfire = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.Soulfire, 10);
        var hand = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.HandOfGuldan, 11);
        var deck = new[]
        {
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.ShredOfTime, 30),
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.BonewebEgg, 31),
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.WalkingDead, 32),
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.DukeOfBelow, 33)
        };
        var state = CreateState(new[] { soulfire, hand }, deck: deck, heroHealth: 10);

        var result = _engine.Apply(state, new PlayCardAction(PlayerSide.Friendly, 10, 200));

        Assert.Equal(7, result.State.Friendly.Hero.Health);
        Assert.Equal(new[] { 31, 32, 33 }, result.State.Friendly.Hand.Select(card => card.EntityId));
        Assert.Contains(result.Events, ruleEvent => ruleEvent.Type == "casts_when_drawn");
        AssertValidOutcomes(result);
    }

    private RuleGameState PlayAndChooseDiscard(RuleGameState state, int occultistEntityId, int targetEntityId, int choiceId)
    {
        var played = _engine.Apply(state, new PlayCardAction(PlayerSide.Friendly, occultistEntityId));
        var target = played.State.Friendly.Hand.Single(card => card.EntityId == targetEntityId);
        var withChoice = played.State with
        {
            PendingChoice = new PendingChoiceState(
                choiceId,
                "HAND_DISCARD",
                DiscardWarlockCardIds.OcularOccultist,
                ImmutableArray.Create(new ChoiceCandidateState(target.EntityId, target.CardId)),
                occultistEntityId)
        };
        return _engine.Apply(withChoice, new SelectChoiceAction(PlayerSide.Friendly, choiceId, targetEntityId)).State;
    }

    private static void AssertValidOutcomes(TransitionResult result)
    {
        Assert.Empty(RuleStateValidator.Validate(result.State));
        Assert.All(result.Branches, branch => Assert.Empty(RuleStateValidator.Validate(branch.State)));
        if (!result.Branches.IsEmpty)
            Assert.Equal(1d, result.Branches.Sum(branch => branch.Probability), 10);
    }

    private static RuleGameState CreateState(
        HandCardState[] hand,
        int mana = 10,
        IEnumerable<MinionState>? board = null,
        HandCardState[]? deck = null,
        int heroHealth = 30)
    {
        var friendly = PlayerState.Create(
            new HeroState(100, "HERO", heroHealth, 30),
            new HeroPowerState(101, "POWER", 2),
            new ManaState(mana, 0, 10 - mana, 10, 0, 0),
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
