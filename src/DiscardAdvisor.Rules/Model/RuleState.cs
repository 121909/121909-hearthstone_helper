using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace DiscardAdvisor.Rules.Model;

public enum PlayerSide
{
    Friendly,
    Opponent
}

public enum RuleCardType
{
    Minion,
    Spell,
    Weapon,
    Location
}

public enum TargetKind
{
    None,
    AnyCharacter,
    EnemyCharacter,
    FriendlyCharacter,
    EnemyMinion,
    FriendlyMinion
}

public sealed record ManaState(int Available, int Temporary, int Spent, int Maximum, int Locked, int OverloadedNextTurn)
{
    public ManaState Spend(int amount)
    {
        if (amount < 0 || amount > Available)
            throw new ArgumentOutOfRangeException(nameof(amount));
        var temporarySpent = Math.Min(Temporary, amount);
        return this with
        {
            Available = Available - amount,
            Temporary = Temporary - temporarySpent,
            Spent = Spent + amount - temporarySpent
        };
    }
}

public sealed record HeroState(
    int EntityId,
    string CardId,
    int Health,
    int MaxHealth,
    int Armor = 0,
    int Attack = 0,
    bool Frozen = false,
    bool Immune = false,
    int AttacksThisTurn = 0,
    int MaxAttacksThisTurn = 1);

public sealed record HeroPowerState(
    int EntityId,
    string CardId,
    int Cost,
    int UsesThisTurn = 0,
    int MaxUsesThisTurn = 1,
    TargetKind TargetKind = TargetKind.None);

public sealed record WeaponState(int EntityId, string CardId, int Attack, int Durability);

public sealed record HandCardState(
    int EntityId,
    string CardId,
    int Cost,
    RuleCardType CardType,
    int Attack = 0,
    int Health = 0,
    TargetKind TargetKind = TargetKind.None,
    int LocationDurability = 0,
    int LocationCooldown = 0,
    bool Taunt = false,
    bool Rush = false,
    bool Charge = false,
    bool Temporary = false);

public sealed record MinionState(
    int EntityId,
    string CardId,
    int BoardPosition,
    int Attack,
    int Health,
    int MaxHealth,
    int AttacksThisTurn = 0,
    int MaxAttacksThisTurn = 1,
    bool Frozen = false,
    bool Dormant = false,
    bool Taunt = false,
    bool Rush = false,
    bool Charge = false,
    bool Stealth = false,
    bool Immune = false,
    bool SummonedThisTurn = false);

public sealed record LocationState(
    int EntityId,
    string CardId,
    int BoardPosition,
    int Durability,
    int Cooldown,
    int ActivationCooldown);

public sealed record ZoneCardState(int EntityId, string CardId);

public sealed record ChoiceCandidateState(int EntityId, string CardId);

public sealed record PendingChoiceState(
    int ChoiceId,
    string ChoiceType,
    string SourceCardId,
    ImmutableArray<ChoiceCandidateState> Candidates);

public sealed record PlayerState(
    HeroState Hero,
    HeroPowerState HeroPower,
    ManaState Mana,
    ImmutableArray<HandCardState> Hand,
    ImmutableArray<MinionState> Board,
    ImmutableArray<LocationState> Locations,
    ImmutableArray<HandCardState> Deck,
    ImmutableArray<ZoneCardState> Graveyard,
    int Fatigue,
    WeaponState? Weapon = null)
{
    public int BoardCount => Board.Length + Locations.Length;

    public static PlayerState Create(
        HeroState hero,
        HeroPowerState heroPower,
        ManaState mana,
        IEnumerable<HandCardState>? hand = null,
        IEnumerable<MinionState>? board = null,
        IEnumerable<LocationState>? locations = null,
        IEnumerable<HandCardState>? deck = null,
        IEnumerable<ZoneCardState>? graveyard = null,
        int fatigue = 0,
        WeaponState? weapon = null) => new(
            hero,
            heroPower,
            mana,
            (hand ?? Enumerable.Empty<HandCardState>()).ToImmutableArray(),
            NormalizeBoard(board ?? Enumerable.Empty<MinionState>()),
            NormalizeLocations(locations ?? Enumerable.Empty<LocationState>()),
            (deck ?? Enumerable.Empty<HandCardState>()).ToImmutableArray(),
            (graveyard ?? Enumerable.Empty<ZoneCardState>()).ToImmutableArray(),
            fatigue,
            weapon);

    public PlayerState NormalizePositions() => this with
    {
        Board = NormalizeBoard(Board),
        Locations = NormalizeLocations(Locations)
    };

    private static ImmutableArray<MinionState> NormalizeBoard(IEnumerable<MinionState> board) => board
        .OrderBy(minion => minion.BoardPosition)
        .Select((minion, index) => minion with { BoardPosition = index + 1 })
        .ToImmutableArray();

    private static ImmutableArray<LocationState> NormalizeLocations(IEnumerable<LocationState> locations) => locations
        .OrderBy(location => location.BoardPosition)
        .Select((location, index) => location with { BoardPosition = index + 1 })
        .ToImmutableArray();
}

public sealed record RuleGameState(
    int TurnNumber,
    PlayerSide ActiveSide,
    PlayerState Friendly,
    PlayerState Opponent,
    int NextEntityId = 10000,
    PendingChoiceState? PendingChoice = null)
{
    public PlayerState Player(PlayerSide side) => side == PlayerSide.Friendly ? Friendly : Opponent;

    public RuleGameState WithPlayer(PlayerSide side, PlayerState player) => side == PlayerSide.Friendly
        ? this with { Friendly = player }
        : this with { Opponent = player };

    public static PlayerSide Other(PlayerSide side) => side == PlayerSide.Friendly ? PlayerSide.Opponent : PlayerSide.Friendly;

    public RuleGameState AllocateEntity(out int entityId)
    {
        entityId = NextEntityId;
        return this with { NextEntityId = NextEntityId + 1 };
    }
}
