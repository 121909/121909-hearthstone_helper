using System;
using System.Collections.Immutable;
using System.Linq;
using DiscardAdvisor.Domain;
using DiscardAdvisor.Rules.Model;
using Xunit;

namespace DiscardAdvisor.Rules.Tests;

public sealed class DiscardWarlockCardTests
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

    [Fact]
    public void CatalogCoversAllSeventeenProfileEntries()
    {
        Assert.Equal(17, DiscardWarlockCardCatalog.TargetCardIds.Count);
        Assert.Equal(
            TargetDeckProfile.Cards.Select(card => card.CardId).OrderBy(id => id, StringComparer.Ordinal),
            DiscardWarlockCardCatalog.TargetCardIds.OrderBy(id => id, StringComparer.Ordinal));
        foreach (var cardId in DiscardWarlockCardCatalog.TargetCardIds)
            Assert.Equal(cardId, DiscardWarlockCardCatalog.Create(cardId, 1).CardId);
    }

    [Theory]
    [MemberData(nameof(TargetCards))]
    public void EveryTargetCardHasALegalPlayPath(string cardId)
    {
        var card = DiscardWarlockCardCatalog.Create(cardId, 10);
        var state = CreateState(hand: new[] { card });
        var target = card.TargetKind == TargetKind.None ? null : (int?)200;

        var result = _engine.Apply(state, new PlayCardAction(PlayerSide.Friendly, 10, target));

        Assert.True(result.IsLegal);
        Assert.Contains(result.Events, ruleEvent => ruleEvent.Type == "play_card" && ruleEvent.CardId == cardId);
    }

    [Fact]
    public void EntropicContinuityBuffsExistingBoardThenAddsTwoShreds()
    {
        var spell = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.EntropicContinuity, 10);
        var minion = new MinionState(20, "MINION", 1, 2, 3, 3);
        var state = CreateState(hand: new[] { spell }, board: new[] { minion });

        var result = _engine.Apply(state, new PlayCardAction(PlayerSide.Friendly, 10));

        Assert.True(result.IsLegal);
        Assert.Equal((3, 4, 4), (result.State.Friendly.Board[0].Attack, result.State.Friendly.Board[0].Health, result.State.Friendly.Board[0].MaxHealth));
        Assert.Equal(2, result.State.Friendly.Deck.Count(card => card.CardId == DiscardWarlockCardIds.ShredOfTime));
        Assert.False(result.State.Friendly.DeckOrderKnown);
    }

    [Fact]
    public void PartyFiendSummonsTwoFelbeastsBeforeSelfDamage()
    {
        var card = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.PartyFiend, 10);
        var state = CreateState(hand: new[] { card }, heroHealth: 10);

        var result = _engine.Apply(state, new PlayCardAction(PlayerSide.Friendly, 10));

        Assert.Equal(3, result.State.Friendly.Board.Length);
        Assert.Equal(7, result.State.Friendly.Hero.Health);
        Assert.Equal(2, result.State.Friendly.Board.Count(minion => minion.CardId == DiscardWarlockCardIds.Felbeast));
        Assert.Equal("damage", result.Events.Last().Type);
    }

    [Fact]
    public void PartyFiendInsertsTokensToItsRightAndRecordsFailedAttempts()
    {
        var card = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.PartyFiend, 10);
        var left = new MinionState(20, "LEFT", 1, 1, 1, 1);
        var right = new MinionState(21, "RIGHT", 2, 1, 1, 1);
        var filler = Enumerable.Range(0, 3)
            .Select(index => new MinionState(30 + index, $"FILLER_{index}", 3 + index, 1, 1, 1));
        var state = CreateState(hand: new[] { card }, board: new[] { left, right }.Concat(filler).ToArray());

        var result = _engine.Apply(
            state,
            new PlayCardAction(PlayerSide.Friendly, card.EntityId, BoardPosition: 2));

        Assert.Equal(7, result.State.Friendly.BoardCount);
        Assert.Equal(
            new[] { "LEFT", DiscardWarlockCardIds.PartyFiend, DiscardWarlockCardIds.Felbeast, "RIGHT" },
            result.State.Friendly.Board.Take(4).Select(minion => minion.CardId));
        Assert.Single(result.Events.Where(ruleEvent => ruleEvent.Type == "summon_failed_board_full"));
        Assert.True(RuleStateValidator.IsValid(result.State));
    }

    [Fact]
    public void PlatysaurDrawsOneCard()
    {
        var platysaur = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.Platysaur, 10);
        var deckCard = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.BonewebEgg, 30);
        var state = CreateState(hand: new[] { platysaur }, deck: new[] { deckCard });

        var result = _engine.Apply(state, new PlayCardAction(PlayerSide.Friendly, 10));

        Assert.Contains(result.State.Friendly.Hand, card => card.EntityId == 30);
        Assert.Contains(result.Events, ruleEvent => ruleEvent.Type == "draw");
    }

    [Fact]
    public void PlatysaurBindsTheFinalHandEntityAfterShredReplacement()
    {
        var platysaur = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.Platysaur, 10);
        var shred = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.ShredOfTime, 30);
        var replacement = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.BonewebEgg, 31);
        var state = CreateState(hand: new[] { platysaur }, deck: new[] { shred, replacement }, heroHealth: 10);

        var result = _engine.Apply(state, new PlayCardAction(PlayerSide.Friendly, platysaur.EntityId));

        Assert.Equal(replacement.EntityId, Assert.Single(result.State.Bindings).Value);
        Assert.Equal(replacement.EntityId, Assert.Single(result.State.Friendly.Hand).EntityId);
        Assert.DoesNotContain(result.State.Bindings.Values, entityId => entityId == shred.EntityId);
    }

    [Fact]
    public void OcularOccultistDoesNotRequestChoiceWithAnEmptyRemainingHand()
    {
        var occultist = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.OcularOccultist, 10);

        var result = _engine.Apply(
            CreateState(hand: new[] { occultist }),
            new PlayCardAction(PlayerSide.Friendly, occultist.EntityId));

        Assert.True(result.IsLegal);
        Assert.DoesNotContain(result.Events, ruleEvent => ruleEvent.Type == "hand_discard_choice_pending");
    }

    [Fact]
    public void SoulfireDealsDamageBeforeRequestingRandomDiscard()
    {
        var soulfire = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.Soulfire, 10);
        var discarded = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.HandOfGuldan, 11);
        var state = CreateState(hand: new[] { soulfire, discarded }, opponentHealth: 10);

        var result = _engine.Apply(state, new PlayCardAction(PlayerSide.Friendly, 10, 200));

        Assert.Equal(6, result.State.Opponent.Hero.Health);
        var damageIndex = result.Events.Select((ruleEvent, index) => (ruleEvent, index))
            .First(value => value.ruleEvent.Type == "damage").index;
        var discardIndex = result.Events.Select((ruleEvent, index) => (ruleEvent, index))
            .First(value => value.ruleEvent.Type == "discard").index;
        Assert.True(damageIndex < discardIndex);
    }

    [Fact]
    public void SoulfireRemovesDivineShieldWithoutDamagingTheMinion()
    {
        var soulfire = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.Soulfire, 10);
        var shielded = new MinionState(30, "SHIELDED", 1, 2, 5, 5, DivineShield: true);
        var state = CreateState(hand: new[] { soulfire }, opponentBoard: new[] { shielded });

        var result = _engine.Apply(state, new PlayCardAction(PlayerSide.Friendly, soulfire.EntityId, shielded.EntityId));

        var minion = Assert.Single(result.State.Opponent.Board);
        Assert.Equal(5, minion.Health);
        Assert.False(minion.DivineShield);
        Assert.Contains(result.Events, ruleEvent => ruleEvent.Type == "divine_shield_lost");
    }

    [Fact]
    public void SoulariumDrawsThreeTemporaryCardsAndCastsShred()
    {
        var soularium = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.Soularium, 10);
        var deck = new[]
        {
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.BonewebEgg, 30),
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.ShredOfTime, 31),
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.HandOfGuldan, 32),
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.WalkingDead, 33)
        };
        var state = CreateState(hand: new[] { soularium }, deck: deck, heroHealth: 10);

        var result = _engine.Apply(state, new PlayCardAction(PlayerSide.Friendly, 10));

        Assert.Equal(3, result.State.Friendly.Hand.Length);
        Assert.All(result.State.Friendly.Hand, card => Assert.True(card.Temporary));
        Assert.Equal(7, result.State.Friendly.Hero.Health);
        Assert.Contains(result.Events, ruleEvent => ruleEvent.Type == "casts_when_drawn");
    }

    [Fact]
    public void LifeTapDrawsThenDealsTwoSelfDamage()
    {
        var deckCard = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.BonewebEgg, 30);
        var state = CreateState(
            deck: new[] { deckCard },
            heroHealth: 10,
            heroPowerCardId: DiscardWarlockCardIds.LifeTap);

        var result = _engine.Apply(state, new UseHeroPowerAction(PlayerSide.Friendly));

        Assert.True(result.IsLegal);
        Assert.Equal(8, result.State.Friendly.Hero.Health);
        Assert.Equal(8, result.State.Friendly.Mana.Available);
        Assert.Equal(deckCard.EntityId, Assert.Single(result.State.Friendly.Hand).EntityId);
        Assert.True(EventIndex(result.Events, "draw") < EventIndex(result.Events, "damage"));
    }

    [Fact]
    public void LethalShredDrawStopsLifeTapBeforeReplacementAndSelfDamage()
    {
        var shred = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.ShredOfTime, 30);
        var replacement = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.BonewebEgg, 31);
        var state = CreateState(
            deck: new[] { shred, replacement },
            heroHealth: 3,
            heroPowerCardId: DiscardWarlockCardIds.LifeTap);

        var result = _engine.Apply(state, new UseHeroPowerAction(PlayerSide.Friendly));

        Assert.Equal(0, result.State.Friendly.Hero.Health);
        Assert.Empty(result.State.Friendly.Hand);
        Assert.Equal(replacement.EntityId, Assert.Single(result.State.Friendly.Deck).EntityId);
        Assert.Single(result.Events.Where(ruleEvent => ruleEvent.Type == "casts_when_drawn"));
        Assert.DoesNotContain(result.Events, ruleEvent => ruleEvent.Type == "damage");
    }

    [Fact]
    public void ShredCastsInsteadOfBurningWhenHandIsFull()
    {
        var fullHand = Enumerable.Range(0, 10)
            .Select(index => DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.BonewebEgg, 10 + index))
            .ToArray();
        var shred = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.ShredOfTime, 30);
        var replacement = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.WalkingDead, 31);
        var state = CreateState(
            hand: fullHand,
            deck: new[] { shred, replacement },
            heroHealth: 10,
            heroPowerCardId: DiscardWarlockCardIds.LifeTap);

        var result = _engine.Apply(state, new UseHeroPowerAction(PlayerSide.Friendly));

        Assert.Equal(5, result.State.Friendly.Hero.Health);
        Assert.Equal(10, result.State.Friendly.Hand.Length);
        Assert.Contains(result.Events, ruleEvent =>
            ruleEvent.Type == "casts_when_drawn" && ruleEvent.CardId == DiscardWarlockCardIds.ShredOfTime);
        Assert.Contains(result.Events, ruleEvent =>
            ruleEvent.Type == "burn" && ruleEvent.CardId == DiscardWarlockCardIds.WalkingDead);
        Assert.DoesNotContain(result.Events, ruleEvent =>
            ruleEvent.Type == "burn" && ruleEvent.CardId == DiscardWarlockCardIds.ShredOfTime);
    }

    [Fact]
    public void LethalSoulfireDoesNotResolveItsDiscardStep()
    {
        var soulfire = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.Soulfire, 10);
        var retained = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.HandOfGuldan, 11);
        var state = CreateState(hand: new[] { soulfire, retained }, opponentHealth: 4);

        var result = _engine.Apply(state, new PlayCardAction(PlayerSide.Friendly, soulfire.EntityId, 200));

        Assert.Equal(0, result.State.Opponent.Hero.Health);
        Assert.Empty(result.Branches);
        Assert.Equal(retained.EntityId, Assert.Single(result.State.Friendly.Hand).EntityId);
        Assert.Equal(0, result.State.Friendly.DiscardCount);
        Assert.DoesNotContain(result.Events, ruleEvent => ruleEvent.Type == "discard");
    }

    [Fact]
    public void ChamberAndChronoclawsUseLockedStats()
    {
        var chamber = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.ChamberOfViscidus, 10);
        var claws = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.Chronoclaws, 11);

        var chamberResult = _engine.Apply(CreateState(hand: new[] { chamber }), new PlayCardAction(PlayerSide.Friendly, 10));
        var clawsResult = _engine.Apply(CreateState(hand: new[] { claws }), new PlayCardAction(PlayerSide.Friendly, 11));

        Assert.Equal(2, chamberResult.State.Friendly.Locations[0].Durability);
        Assert.Equal((4, 3), (clawsResult.State.Friendly.Weapon!.Attack, clawsResult.State.Friendly.Weapon.Durability));
        Assert.Equal(4, clawsResult.State.Friendly.Hero.Attack);
    }

    [Fact]
    public void DukeStatsUseDiscardCountAtCardCreation()
    {
        var duke = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.DukeOfBelow, 10, discardCount: 3);

        Assert.Equal((8, 8, true), (duke.Attack, duke.Health, duke.Rush));
    }

    [Fact]
    public void CursedCatacombsUsesOnlyActualChoiceCandidates()
    {
        var candidate = new ChoiceCandidateState(50, DiscardWarlockCardIds.HandOfGuldan);
        var deckCard = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.HandOfGuldan, 30);
        var state = CreateState(deck: new[] { deckCard }) with
        {
            PendingChoice = new PendingChoiceState(
                7,
                "DISCOVER",
                DiscardWarlockCardIds.CursedCatacombs,
                ImmutableArray.Create(candidate))
        };

        var invalid = _engine.Apply(state, new SelectChoiceAction(PlayerSide.Friendly, 7, 51));
        var selected = _engine.Apply(state, new SelectChoiceAction(PlayerSide.Friendly, 7, 50));

        Assert.Equal(RuleError.InvalidTarget, invalid.Error);
        var generated = Assert.Single(selected.State.Friendly.Hand);
        Assert.True(generated.Temporary);
        Assert.Equal(DiscardWarlockCardIds.HandOfGuldan, generated.CardId);
        Assert.Empty(selected.State.Friendly.Deck);
    }

    private static RuleGameState CreateState(
        HandCardState[]? hand = null,
        HandCardState[]? deck = null,
        MinionState[]? board = null,
        MinionState[]? opponentBoard = null,
        int heroHealth = 30,
        int opponentHealth = 30,
        string heroPowerCardId = "HERO_POWER")
    {
        var friendly = PlayerState.Create(
            new HeroState(100, "HERO", heroHealth, 30),
            new HeroPowerState(101, heroPowerCardId, 2),
            new ManaState(10, 0, 0, 10, 0, 0),
            hand,
            board,
            deck: deck);
        var opponent = PlayerState.Create(
            new HeroState(200, "OPPONENT_HERO", opponentHealth, 30),
            new HeroPowerState(201, "OPPONENT_POWER", 2),
            new ManaState(10, 0, 0, 10, 0, 0),
            board: opponentBoard);
        return new RuleGameState(1, PlayerSide.Friendly, friendly, opponent);
    }

    private static int EventIndex(ImmutableArray<RuleEvent> events, string type) => events
        .Select((ruleEvent, index) => (ruleEvent, index))
        .First(value => value.ruleEvent.Type == type).index;
}
