using System.Collections.Immutable;
using System.Linq;
using DiscardAdvisor.Rules;
using DiscardAdvisor.Rules.Model;
using Xunit;

namespace DiscardAdvisor.Search.Tests;

public sealed class LegalActionEnumeratorTests
{
    private readonly LegalActionEnumerator _enumerator = new();
    private readonly DiscardWarlockRuleEngine _rules = new();

    [Fact]
    public void EnumeratesEverySharedBoardInsertionPosition()
    {
        var card = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.WalkingDead, 10);
        var board = new[] { Minion(20, 1) };
        var locations = new[] { new LocationState(30, "LOCATION", 2, 2, 0, 2) };
        var state = CreateState(hand: new[] { card }, board: board, locations: locations);

        var actions = _enumerator.Enumerate(state).OfType<PlayCardAction>()
            .Where(action => action.SourceEntityId == 10)
            .ToArray();

        Assert.Equal(new int?[] { 1, 2, 3 }, actions.Select(action => action.BoardPosition));
    }

    [Fact]
    public void TargetedSpellEnumeratesCharactersButExcludesStealthedEnemy()
    {
        var soulfire = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.Soulfire, 10);
        var friendlyMinion = Minion(20, 1);
        var visibleEnemy = Minion(30, 1);
        var stealthedEnemy = Minion(31, 2) with { Stealth = true };
        var state = CreateState(
            hand: new[] { soulfire },
            board: new[] { friendlyMinion },
            opponentBoard: new[] { visibleEnemy, stealthedEnemy });

        var targets = _enumerator.Enumerate(state).OfType<PlayCardAction>()
            .Where(action => action.SourceEntityId == 10)
            .Select(action => action.TargetEntityId)
            .ToArray();

        Assert.Equal(new int?[] { 20, 100, 30, 200 }, targets);
        Assert.DoesNotContain((int?)31, targets);
    }

    [Fact]
    public void AttackEnumerationRespectsTaunt()
    {
        var attacker = Minion(20, 1, attack: 3, health: 3);
        var taunt = Minion(30, 1) with { Taunt = true };
        var other = Minion(31, 2);
        var state = CreateState(board: new[] { attacker }, opponentBoard: new[] { taunt, other });

        var attacks = _enumerator.Enumerate(state).OfType<AttackAction>()
            .Where(action => action.SourceEntityId == 20)
            .ToArray();

        var attack = Assert.Single(attacks);
        Assert.Equal(30, attack.TargetEntityId);
    }

    [Fact]
    public void PendingChoiceLocksEnumerationToActualCandidates()
    {
        var target = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.HandOfGuldan, 10);
        var state = CreateState(hand: new[] { target }) with
        {
            PendingChoice = new PendingChoiceState(
                7,
                "HAND_DISCARD",
                DiscardWarlockCardIds.OcularOccultist,
                ImmutableArray.Create(new ChoiceCandidateState(10, target.CardId)),
                50)
        };

        var actions = _enumerator.Enumerate(state);

        var choice = Assert.Single(actions);
        Assert.Equal(new SelectChoiceAction(PlayerSide.Friendly, 7, 10), choice);
    }

    [Fact]
    public void EnumeratesGenericAvailableLocationTargetsAndEndTurn()
    {
        var location = new LocationState(40, "GENERIC_LOCATION", 1, 2, 0, 2);
        var state = CreateState(locations: new[] { location });

        var actions = _enumerator.Enumerate(state);

        Assert.Contains(actions, action => action is UseLocationAction use && use.SourceEntityId == 40);
        Assert.Contains(new EndTurnAction(PlayerSide.Friendly), actions);
    }

    [Fact]
    public void DoesNotEnumerateUnavailableHeroPowerOrLocation()
    {
        var location = new LocationState(40, "GENERIC_LOCATION", 1, 2, 0, 2, false);
        var state = CreateState(locations: new[] { location });
        state = state.WithPlayer(PlayerSide.Friendly, state.Friendly with
        {
            HeroPower = state.Friendly.HeroPower with { Available = false }
        });

        var actions = _enumerator.Enumerate(state);

        Assert.DoesNotContain(actions, action => action is UseHeroPowerAction);
        Assert.DoesNotContain(actions, action => action is UseLocationAction);
    }

    [Fact]
    public void EveryEnumeratedActionIsAcceptedByRuleEngine()
    {
        var soulfire = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.Soulfire, 10);
        var walkingDead = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.WalkingDead, 11);
        var attacker = Minion(20, 1, attack: 2, health: 2);
        var state = CreateState(
            hand: new[] { soulfire, walkingDead },
            board: new[] { attacker },
            opponentBoard: new[] { Minion(30, 1) });

        var actions = _enumerator.Enumerate(state);

        Assert.NotEmpty(actions);
        Assert.All(actions, action => Assert.True(_rules.Apply(state, action).IsLegal, action.ToString()));
    }

    private static RuleGameState CreateState(
        HandCardState[]? hand = null,
        MinionState[]? board = null,
        MinionState[]? opponentBoard = null,
        LocationState[]? locations = null)
    {
        var friendly = PlayerState.Create(
            new HeroState(100, "HERO", 30, 30),
            new HeroPowerState(101, "POWER", 2),
            new ManaState(10, 0, 0, 10, 0, 0),
            hand,
            board,
            locations,
            weapon: new WeaponState(102, "WEAPON", 1, 2));
        var opponent = PlayerState.Create(
            new HeroState(200, "OPPONENT_HERO", 30, 30),
            new HeroPowerState(201, "OPPONENT_POWER", 2),
            new ManaState(0, 0, 0, 0, 0, 0),
            board: opponentBoard);
        return new RuleGameState(1, PlayerSide.Friendly, friendly, opponent);
    }

    private static MinionState Minion(int entityId, int position, int attack = 1, int health = 1) =>
        new(entityId, $"M{entityId}", position, attack, health, health);
}
