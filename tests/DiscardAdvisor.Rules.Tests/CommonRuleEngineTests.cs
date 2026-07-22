using System;
using System.Collections.Generic;
using System.Linq;
using DiscardAdvisor.Rules.Model;
using Xunit;

namespace DiscardAdvisor.Rules.Tests;

public sealed class CommonRuleEngineTests
{
    private readonly CommonRuleEngine _engine = new();

    [Fact]
    public void PlaysMinionWithDynamicCostAndStablePosition()
    {
        var card = new HandCardState(10, "MINION", 2, RuleCardType.Minion, 3, 4);
        var state = CreateState(friendly: CreatePlayer(hand: new[] { card }, mana: 3));

        var result = _engine.Apply(state, new PlayCardAction(PlayerSide.Friendly, 10, BoardPosition: 1));

        Assert.True(result.IsLegal);
        Assert.Equal(1, result.State.Friendly.Mana.Available);
        Assert.Empty(result.State.Friendly.Hand);
        var minion = Assert.Single(result.State.Friendly.Board);
        Assert.Equal((3, 4, 1), (minion.Attack, minion.Health, minion.BoardPosition));
        Assert.True(minion.SummonedThisTurn);
    }

    [Fact]
    public void RejectsInsufficientManaWithoutChangingState()
    {
        var card = new HandCardState(10, "EXPENSIVE", 4, RuleCardType.Spell);
        var state = CreateState(friendly: CreatePlayer(hand: new[] { card }, mana: 3));

        var result = _engine.Apply(state, new PlayCardAction(PlayerSide.Friendly, 10));

        Assert.False(result.IsLegal);
        Assert.Equal(RuleError.InsufficientMana, result.Error);
        Assert.Same(state, result.State);
    }

    [Fact]
    public void RejectsMinionWhenSevenBoardSlotsAreOccupied()
    {
        var board = Enumerable.Range(0, 7).Select(index => Minion(100 + index, index + 1));
        var card = new HandCardState(10, "MINION", 1, RuleCardType.Minion, 1, 1);
        var state = CreateState(friendly: CreatePlayer(hand: new[] { card }, board: board, mana: 1));

        var result = _engine.Apply(state, new PlayCardAction(PlayerSide.Friendly, 10));

        Assert.Equal(RuleError.BoardFull, result.Error);
    }

    [Fact]
    public void FullHandBurnsDrawnCard()
    {
        var hand = Enumerable.Range(0, 10).Select(index => new HandCardState(10 + index, $"H{index}", 1, RuleCardType.Spell));
        var deckCard = new HandCardState(99, "DRAWN", 1, RuleCardType.Spell);
        var state = CreateState(friendly: CreatePlayer(hand: hand, deck: new[] { deckCard }));

        var result = _engine.DrawCard(state, PlayerSide.Friendly);

        Assert.True(result.IsLegal);
        Assert.Equal(10, result.State.Friendly.Hand.Length);
        Assert.Empty(result.State.Friendly.Deck);
        Assert.Contains(result.State.Friendly.Graveyard, card => card.EntityId == 99);
        Assert.Contains(result.Events, ruleEvent => ruleEvent.Type == "burn");
    }

    [Fact]
    public void EmptyDeckDealsIncreasingFatigueDamage()
    {
        var state = CreateState(friendly: CreatePlayer(heroHealth: 10, fatigue: 1));

        var result = _engine.DrawCard(state, PlayerSide.Friendly);

        Assert.Equal(8, result.State.Friendly.Hero.Health);
        Assert.Equal(2, result.State.Friendly.Fatigue);
    }

    [Fact]
    public void TauntBlocksOtherAttackTargets()
    {
        var attacker = Minion(10, 1, attack: 3, health: 3);
        var taunt = Minion(20, 1, taunt: true);
        var state = CreateState(
            friendly: CreatePlayer(board: new[] { attacker }),
            opponent: CreatePlayer(board: new[] { taunt }, heroId: 200));

        var result = _engine.Apply(state, new AttackAction(PlayerSide.Friendly, 10, 200));

        Assert.Equal(RuleError.TauntBlocksTarget, result.Error);
    }

    [Fact]
    public void RushMinionCanAttackMinionButNotHeroOnSummonTurn()
    {
        var attacker = Minion(10, 1, rush: true, summoned: true);
        var defender = Minion(20, 1);
        var state = CreateState(
            friendly: CreatePlayer(board: new[] { attacker }),
            opponent: CreatePlayer(board: new[] { defender }, heroId: 200));

        var heroAttack = _engine.Apply(state, new AttackAction(PlayerSide.Friendly, 10, 200));
        var minionAttack = _engine.Apply(state, new AttackAction(PlayerSide.Friendly, 10, 20));

        Assert.Equal(RuleError.RushCannotAttackHero, heroAttack.Error);
        Assert.True(minionAttack.IsLegal);
    }

    [Fact]
    public void FrozenAndExhaustedMinionsCannotAttack()
    {
        var frozen = Minion(10, 1) with { Frozen = true };
        var exhausted = Minion(11, 2) with { AttacksThisTurn = 1 };
        var opponent = CreatePlayer(heroId: 200);

        var frozenResult = _engine.Apply(
            CreateState(friendly: CreatePlayer(board: new[] { frozen }), opponent: opponent),
            new AttackAction(PlayerSide.Friendly, 10, 200));
        var exhaustedResult = _engine.Apply(
            CreateState(friendly: CreatePlayer(board: new[] { exhausted }), opponent: opponent),
            new AttackAction(PlayerSide.Friendly, 11, 200));

        Assert.Equal(RuleError.Frozen, frozenResult.Error);
        Assert.Equal(RuleError.Exhausted, exhaustedResult.Error);
    }

    [Fact]
    public void CombatIsSimultaneousAndDeadMinionsReleaseBoardSpace()
    {
        var attacker = Minion(10, 1, attack: 2, health: 2);
        var defender = Minion(20, 1, attack: 2, health: 2);
        var state = CreateState(
            friendly: CreatePlayer(board: new[] { attacker }),
            opponent: CreatePlayer(board: new[] { defender }));

        var result = _engine.Apply(state, new AttackAction(PlayerSide.Friendly, 10, 20));

        Assert.Empty(result.State.Friendly.Board);
        Assert.Empty(result.State.Opponent.Board);
        Assert.Equal(2, result.Events.Count(ruleEvent => ruleEvent.Type == "death"));
    }

    [Fact]
    public void DivineShieldAbsorbsCombatDamageBeforeHealth()
    {
        var attacker = Minion(10, 1, attack: 3, health: 3);
        var defender = Minion(20, 1, attack: 2, health: 2, divineShield: true);
        var state = CreateState(
            friendly: CreatePlayer(board: new[] { attacker }),
            opponent: CreatePlayer(board: new[] { defender }));

        var result = _engine.Apply(state, new AttackAction(PlayerSide.Friendly, 10, 20));

        Assert.Equal(1, result.State.Friendly.Board.Single().Health);
        var shielded = result.State.Opponent.Board.Single();
        Assert.Equal(2, shielded.Health);
        Assert.False(shielded.DivineShield);
        Assert.Contains(result.Events, ruleEvent => ruleEvent.Type == "divine_shield_lost");
    }

    [Fact]
    public void PoisonousDestroysAMinionOnlyWhenDamageIsApplied()
    {
        var poisonous = Minion(10, 1, attack: 1, health: 2, poisonous: true);
        var defender = Minion(20, 1, attack: 0, health: 10);
        var shielded = defender with { DivineShield = true };

        var destroyed = _engine.Apply(
            CreateState(
                friendly: CreatePlayer(board: new[] { poisonous }),
                opponent: CreatePlayer(board: new[] { defender })),
            new AttackAction(PlayerSide.Friendly, 10, 20));
        var protectedResult = _engine.Apply(
            CreateState(
                friendly: CreatePlayer(board: new[] { poisonous }),
                opponent: CreatePlayer(board: new[] { shielded })),
            new AttackAction(PlayerSide.Friendly, 10, 20));

        Assert.Empty(destroyed.State.Opponent.Board);
        Assert.Single(protectedResult.State.Opponent.Board);
    }

    [Fact]
    public void LifestealHealsBothOwnersFromSimultaneousCombatDamage()
    {
        var attacker = Minion(10, 1, attack: 3, health: 5, lifesteal: true);
        var defender = Minion(20, 1, attack: 2, health: 5, lifesteal: true);
        var state = CreateState(
            friendly: CreatePlayer(board: new[] { attacker }, heroHealth: 20),
            opponent: CreatePlayer(board: new[] { defender }, heroId: 200, heroHealth: 20));

        var result = _engine.Apply(state, new AttackAction(PlayerSide.Friendly, 10, 20));

        Assert.Equal(23, result.State.Friendly.Hero.Health);
        Assert.Equal(22, result.State.Opponent.Hero.Health);
        Assert.Equal(2, result.Events.Count(ruleEvent => ruleEvent.Type == "heal"));
    }

    [Fact]
    public void HeroAttackConsumesWeaponDurabilityAndArmorAbsorbsRetaliation()
    {
        var friendly = CreatePlayer(heroHealth: 30, heroArmor: 2, weapon: new WeaponState(30, "WEAPON", 3, 1));
        var defender = Minion(20, 1, attack: 4, health: 3);
        var state = CreateState(friendly: friendly, opponent: CreatePlayer(board: new[] { defender }));

        var result = _engine.Apply(state, new AttackAction(PlayerSide.Friendly, 100, 20));

        Assert.Null(result.State.Friendly.Weapon);
        Assert.Equal(28, result.State.Friendly.Hero.Health);
        Assert.Equal(0, result.State.Friendly.Hero.Armor);
        Assert.Empty(result.State.Opponent.Board);
    }

    [Fact]
    public void HeroPowerSpendsManaOncePerTurn()
    {
        var state = CreateState(friendly: CreatePlayer(mana: 3));

        var first = _engine.Apply(state, new UseHeroPowerAction(PlayerSide.Friendly));
        var second = _engine.Apply(first.State, new UseHeroPowerAction(PlayerSide.Friendly));

        Assert.True(first.IsLegal);
        Assert.Equal(1, first.State.Friendly.Mana.Available);
        Assert.Equal(RuleError.Exhausted, second.Error);
    }

    [Fact]
    public void LocationRequiresZeroCooldownAndLosesDurability()
    {
        var location = new LocationState(50, "LOCATION", 1, 2, 0, 2);
        var state = CreateState(friendly: CreatePlayer(locations: new[] { location }));

        var first = _engine.Apply(state, new UseLocationAction(PlayerSide.Friendly, 50, 100));
        var second = _engine.Apply(first.State, new UseLocationAction(PlayerSide.Friendly, 50, 100));

        Assert.True(first.IsLegal);
        Assert.Equal((1, 2), (first.State.Friendly.Locations[0].Durability, first.State.Friendly.Locations[0].Cooldown));
        Assert.Equal(RuleError.LocationUnavailable, second.Error);
    }

    [Fact]
    public void StateValidatorCatchesHandBoardAndManaInvariantViolations()
    {
        var hand = Enumerable.Range(0, 11).Select(index => new HandCardState(10 + index, $"H{index}", 1, RuleCardType.Spell));
        var board = Enumerable.Range(0, 7).Select(index => Minion(100 + index, index + 1));
        var invalidPlayer = CreatePlayer(hand: hand, board: board, locations: new[] { new LocationState(500, "L", 1, 1, 0, 1) }, mana: 10);
        var state = CreateState(friendly: invalidPlayer);

        var errors = RuleStateValidator.Validate(state);

        Assert.Contains("friendly_hand_overflow", errors);
        Assert.Contains("friendly_board_overflow", errors);
    }

    [Fact]
    public void EndingTurnClearsFrozenAndSummonedFlagsForTheEndingPlayer()
    {
        var minion = Minion(10, 1, summoned: true) with { Frozen = true };
        var state = CreateState(friendly: CreatePlayer(board: new[] { minion }));

        var result = _engine.Apply(state, new EndTurnAction(PlayerSide.Friendly));
        var next = _engine.Apply(result.State, new EndTurnAction(PlayerSide.Opponent));

        var cleared = next.State.Friendly.Board.Single();
        Assert.False(cleared.Frozen);
        Assert.False(cleared.SummonedThisTurn);
    }

    [Fact]
    public void MinionsAndLocationsShareOneStableBoardPositionSequence()
    {
        var existingMinion = Minion(20, 1);
        var location = new LocationState(30, "LOCATION", 2, 2, 0, 2);
        var playedMinion = new HandCardState(10, "NEW_MINION", 1, RuleCardType.Minion, 1, 1);
        var state = CreateState(friendly: CreatePlayer(
            hand: new[] { playedMinion },
            board: new[] { existingMinion },
            locations: new[] { location },
            mana: 1));

        var result = _engine.Apply(state, new PlayCardAction(PlayerSide.Friendly, 10, BoardPosition: 2));

        Assert.Equal(new[] { 1, 2 }, result.State.Friendly.Board.Select(minion => minion.BoardPosition));
        Assert.Equal(3, result.State.Friendly.Locations[0].BoardPosition);
        Assert.True(RuleStateValidator.IsValid(result.State));
    }

    private static RuleGameState CreateState(PlayerState? friendly = null, PlayerState? opponent = null) => new(
        1,
        PlayerSide.Friendly,
        friendly ?? CreatePlayer(),
        opponent ?? CreatePlayer(heroId: 200));

    private static PlayerState CreatePlayer(
        IEnumerable<HandCardState>? hand = null,
        IEnumerable<MinionState>? board = null,
        IEnumerable<LocationState>? locations = null,
        IEnumerable<HandCardState>? deck = null,
        int mana = 10,
        int heroId = 100,
        int heroHealth = 30,
        int heroArmor = 0,
        int fatigue = 0,
        WeaponState? weapon = null) => PlayerState.Create(
            new HeroState(heroId, "HERO", heroHealth, 30, heroArmor),
            new HeroPowerState(heroId + 1, "HERO_POWER", 2),
            new ManaState(mana, 0, 10 - mana, 10, 0, 0),
            hand,
            board,
            locations,
            deck,
            fatigue: fatigue,
            weapon: weapon);

    private static MinionState Minion(
        int entityId,
        int position,
        int attack = 1,
        int health = 1,
        bool taunt = false,
        bool rush = false,
        bool summoned = false,
        bool divineShield = false,
        bool poisonous = false,
        bool lifesteal = false) => new(
            entityId,
            $"M{entityId}",
            position,
            attack,
            health,
            health,
            Taunt: taunt,
            Rush: rush,
            SummonedThisTurn: summoned,
            DivineShield: divineShield,
            Poisonous: poisonous,
            Lifesteal: lifesteal);
}
