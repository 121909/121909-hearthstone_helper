using System;
using System.Collections.Generic;

namespace DiscardAdvisor.Domain.Snapshots;

public sealed class DeckEntrySnapshot
{
    public DeckEntrySnapshot(string cardId, int count)
    {
        CardId = cardId ?? throw new ArgumentNullException(nameof(cardId));
        Count = count;
    }

    public string CardId { get; }
    public int Count { get; }
}

public sealed class ZoneCardSnapshot
{
    public ZoneCardSnapshot(int entityId, string cardId, int? createdByEntityId = null)
    {
        EntityId = entityId;
        CardId = cardId ?? throw new ArgumentNullException(nameof(cardId));
        CreatedByEntityId = createdByEntityId;
    }

    public int EntityId { get; }
    public string CardId { get; }
    public int? CreatedByEntityId { get; }
}

public sealed class HeroSnapshot
{
    public HeroSnapshot(
        int entityId,
        string cardId,
        int health,
        int maxHealth,
        int armor,
        int attack,
        bool frozen,
        bool immune,
        int attacksThisTurn,
        int maxAttacksThisTurn)
    {
        EntityId = entityId;
        CardId = cardId ?? throw new ArgumentNullException(nameof(cardId));
        Health = health;
        MaxHealth = maxHealth;
        Armor = armor;
        Attack = attack;
        Frozen = frozen;
        Immune = immune;
        AttacksThisTurn = attacksThisTurn;
        MaxAttacksThisTurn = maxAttacksThisTurn;
    }

    public int EntityId { get; }
    public string CardId { get; }
    public int Health { get; }
    public int MaxHealth { get; }
    public int Armor { get; }
    public int Attack { get; }
    public bool Frozen { get; }
    public bool Immune { get; }
    public int AttacksThisTurn { get; }
    public int MaxAttacksThisTurn { get; }
}

public sealed class WeaponSnapshot
{
    public WeaponSnapshot(int entityId, string cardId, int attack, int durability)
    {
        EntityId = entityId;
        CardId = cardId ?? throw new ArgumentNullException(nameof(cardId));
        Attack = attack;
        Durability = durability;
    }

    public int EntityId { get; }
    public string CardId { get; }
    public int Attack { get; }
    public int Durability { get; }
}

public sealed class HeroPowerSnapshot
{
    public HeroPowerSnapshot(int entityId, string cardId, int cost, bool available, int usesThisTurn, int maxUsesThisTurn)
    {
        EntityId = entityId;
        CardId = cardId ?? throw new ArgumentNullException(nameof(cardId));
        Cost = cost;
        Available = available;
        UsesThisTurn = usesThisTurn;
        MaxUsesThisTurn = maxUsesThisTurn;
    }

    public int EntityId { get; }
    public string CardId { get; }
    public int Cost { get; }
    public bool Available { get; }
    public int UsesThisTurn { get; }
    public int MaxUsesThisTurn { get; }
}

public sealed class ManaSnapshot
{
    public ManaSnapshot(int available, int temporary, int spent, int maximum, int locked, int overloadedNextTurn)
    {
        Available = available;
        Temporary = temporary;
        Spent = spent;
        Maximum = maximum;
        Locked = locked;
        OverloadedNextTurn = overloadedNextTurn;
    }

    public int Available { get; }
    public int Temporary { get; }
    public int Spent { get; }
    public int Maximum { get; }
    public int Locked { get; }
    public int OverloadedNextTurn { get; }
}

public sealed class HandCardSnapshot
{
    public HandCardSnapshot(
        int entityId,
        string cardId,
        int zonePosition,
        int dynamicCost,
        bool temporary,
        int? createdByEntityId = null,
        int? temporaryUntilTurn = null)
    {
        EntityId = entityId;
        CardId = cardId ?? throw new ArgumentNullException(nameof(cardId));
        ZonePosition = zonePosition;
        DynamicCost = dynamicCost;
        CreatedByEntityId = createdByEntityId;
        Temporary = temporary;
        TemporaryUntilTurn = temporaryUntilTurn;
    }

    public int EntityId { get; }
    public string CardId { get; }
    public int ZonePosition { get; }
    public int DynamicCost { get; }
    public int? CreatedByEntityId { get; }
    public bool Temporary { get; }
    public int? TemporaryUntilTurn { get; }
}

public sealed class MinionSnapshot
{
    public MinionSnapshot(
        int entityId,
        string cardId,
        int boardPosition,
        int attack,
        int health,
        int maxHealth,
        int attacksThisTurn,
        int maxAttacksThisTurn,
        bool frozen,
        bool dormant,
        bool taunt,
        bool rush,
        bool charge,
        bool stealth,
        bool divineShield,
        bool poisonous,
        bool lifesteal,
        bool reborn,
        bool immune,
        bool silenced,
        bool summonedThisTurn,
        bool canAttack)
    {
        EntityId = entityId;
        CardId = cardId ?? throw new ArgumentNullException(nameof(cardId));
        BoardPosition = boardPosition;
        Attack = attack;
        Health = health;
        MaxHealth = maxHealth;
        AttacksThisTurn = attacksThisTurn;
        MaxAttacksThisTurn = maxAttacksThisTurn;
        Frozen = frozen;
        Dormant = dormant;
        Taunt = taunt;
        Rush = rush;
        Charge = charge;
        Stealth = stealth;
        DivineShield = divineShield;
        Poisonous = poisonous;
        Lifesteal = lifesteal;
        Reborn = reborn;
        Immune = immune;
        Silenced = silenced;
        SummonedThisTurn = summonedThisTurn;
        CanAttack = canAttack;
    }

    public int EntityId { get; }
    public string CardId { get; }
    public int BoardPosition { get; }
    public int Attack { get; }
    public int Health { get; }
    public int MaxHealth { get; }
    public int AttacksThisTurn { get; }
    public int MaxAttacksThisTurn { get; }
    public bool Frozen { get; }
    public bool Dormant { get; }
    public bool Taunt { get; }
    public bool Rush { get; }
    public bool Charge { get; }
    public bool Stealth { get; }
    public bool DivineShield { get; }
    public bool Poisonous { get; }
    public bool Lifesteal { get; }
    public bool Reborn { get; }
    public bool Immune { get; }
    public bool Silenced { get; }
    public bool SummonedThisTurn { get; }
    public bool CanAttack { get; }
}

public sealed class LocationSnapshot
{
    public LocationSnapshot(int entityId, string cardId, int boardPosition, int durability, int cooldown, bool available)
    {
        EntityId = entityId;
        CardId = cardId ?? throw new ArgumentNullException(nameof(cardId));
        BoardPosition = boardPosition;
        Durability = durability;
        Cooldown = cooldown;
        Available = available;
    }

    public int EntityId { get; }
    public string CardId { get; }
    public int BoardPosition { get; }
    public int Durability { get; }
    public int Cooldown { get; }
    public bool Available { get; }
}

