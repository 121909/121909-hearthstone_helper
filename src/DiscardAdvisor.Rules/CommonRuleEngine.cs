using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using DiscardAdvisor.Rules.Model;

namespace DiscardAdvisor.Rules;

public sealed class CommonRuleEngine
{
    public const int MaximumHandSize = 10;
    public const int MaximumBoardSize = 7;

    public TransitionResult Apply(RuleGameState state, RuleAction action)
    {
        if (state is null)
            throw new ArgumentNullException(nameof(state));
        if (action is null)
            throw new ArgumentNullException(nameof(action));
        if (action.Side != state.ActiveSide)
            return TransitionResult.Illegal(state, RuleError.NotActivePlayer);

        return action switch
        {
            PlayCardAction play => PlayCard(state, play),
            AttackAction attack => Attack(state, attack),
            UseHeroPowerAction heroPower => UseHeroPower(state, heroPower),
            UseLocationAction location => UseLocation(state, location),
            EndTurnAction endTurn => EndTurn(state, endTurn),
            _ => TransitionResult.Illegal(state, RuleError.UnsupportedAction)
        };
    }

    public TransitionResult DrawCard(RuleGameState state, PlayerSide side)
    {
        var player = state.Player(side);
        if (player.Deck.IsEmpty)
        {
            var fatigue = player.Fatigue + 1;
            var damaged = DamageHero(player.Hero, fatigue);
            var next = state.WithPlayer(side, player with { Hero = damaged, Fatigue = fatigue });
            return TransitionResult.Legal(next, new[] { new RuleEvent("fatigue", null, player.Hero.EntityId, fatigue) });
        }

        var card = player.Deck[0];
        var deck = player.Deck.RemoveAt(0);
        if (player.Hand.Length >= MaximumHandSize)
        {
            var graveyard = player.Graveyard.Add(new ZoneCardState(card.EntityId, card.CardId));
            var burned = state.WithPlayer(side, player with { Deck = deck, Graveyard = graveyard });
            return TransitionResult.Legal(burned, new[] { new RuleEvent("burn", card.EntityId, null, 0, card.CardId) });
        }

        var drawn = state.WithPlayer(side, player with { Deck = deck, Hand = player.Hand.Add(card) });
        return TransitionResult.Legal(drawn, new[] { new RuleEvent("draw", card.EntityId, null, 0, card.CardId) });
    }

    private static TransitionResult PlayCard(RuleGameState state, PlayCardAction action)
    {
        var player = state.Player(action.Side);
        var card = player.Hand.FirstOrDefault(candidate => candidate.EntityId == action.SourceEntityId);
        if (card is null)
            return TransitionResult.Illegal(state, RuleError.SourceNotFound);
        if (card.Cost > player.Mana.Available)
            return TransitionResult.Illegal(state, RuleError.InsufficientMana);
        var targetError = ValidateTarget(state, action.Side, card.TargetKind, action.TargetEntityId);
        if (targetError != RuleError.None)
            return TransitionResult.Illegal(state, targetError);
        if ((card.CardType == RuleCardType.Minion || card.CardType == RuleCardType.Location) && player.BoardCount >= MaximumBoardSize)
            return TransitionResult.Illegal(state, RuleError.BoardFull);

        var nextPlayer = player with
        {
            Mana = player.Mana.Spend(card.Cost),
            Hand = player.Hand.Remove(card)
        };
        var events = new List<RuleEvent> { new("play_card", card.EntityId, action.TargetEntityId, 0, card.CardId) };

        switch (card.CardType)
        {
            case RuleCardType.Minion:
                {
                    var position = action.BoardPosition ?? nextPlayer.BoardCount + 1;
                    if (position < 1 || position > nextPlayer.BoardCount + 1)
                        return TransitionResult.Illegal(state, RuleError.InvalidBoardPosition);
                    var shifted = nextPlayer.Board
                        .Select(minion => minion.BoardPosition >= position
                            ? minion with { BoardPosition = minion.BoardPosition + 1 }
                        : minion)
                        .Append(new MinionState(
                            card.EntityId,
                            card.CardId,
                            position,
                            card.Attack,
                            Math.Max(1, card.Health),
                            Math.Max(1, card.Health),
                            Taunt: card.Taunt,
                            Rush: card.Rush,
                            Charge: card.Charge,
                            SummonedThisTurn: true))
                    .OrderBy(minion => minion.BoardPosition)
                    .ToImmutableArray();
                    var shiftedLocations = nextPlayer.Locations.Select(location => location.BoardPosition >= position
                        ? location with { BoardPosition = location.BoardPosition + 1 }
                        : location).ToImmutableArray();
                    nextPlayer = nextPlayer with { Board = shifted, Locations = shiftedLocations };
                    events.Add(new RuleEvent("summon", card.EntityId, null, 0, card.CardId));
                    break;
                }
            case RuleCardType.Weapon:
                nextPlayer = nextPlayer with
                {
                    Weapon = new WeaponState(card.EntityId, card.CardId, card.Attack, Math.Max(1, card.Health)),
                    Graveyard = nextPlayer.Weapon is null
                        ? nextPlayer.Graveyard
                        : nextPlayer.Graveyard.Add(new ZoneCardState(nextPlayer.Weapon.EntityId, nextPlayer.Weapon.CardId))
                };
                break;
            case RuleCardType.Location:
                {
                    var position = action.BoardPosition ?? nextPlayer.BoardCount + 1;
                    if (position < 1 || position > nextPlayer.BoardCount + 1)
                        return TransitionResult.Illegal(state, RuleError.InvalidBoardPosition);
                    var shiftedBoard = nextPlayer.Board.Select(minion => minion.BoardPosition >= position
                        ? minion with { BoardPosition = minion.BoardPosition + 1 }
                        : minion).ToImmutableArray();
                    var shiftedLocations = nextPlayer.Locations.Select(location => location.BoardPosition >= position
                        ? location with { BoardPosition = location.BoardPosition + 1 }
                        : location);
                    nextPlayer = nextPlayer with
                    {
                        Board = shiftedBoard,
                        Locations = shiftedLocations.Append(new LocationState(
                            card.EntityId,
                            card.CardId,
                            position,
                            Math.Max(1, card.LocationDurability),
                            1,
                            Math.Max(1, card.LocationCooldown),
                            false)).OrderBy(location => location.BoardPosition).ToImmutableArray()
                    };
                    break;
                }
            case RuleCardType.Spell:
                nextPlayer = nextPlayer with { Graveyard = nextPlayer.Graveyard.Add(new ZoneCardState(card.EntityId, card.CardId)) };
                break;
            default:
                return TransitionResult.Illegal(state, RuleError.UnsupportedAction);
        }

        return TransitionResult.Legal(state.WithPlayer(action.Side, nextPlayer), events);
    }

    private static TransitionResult Attack(RuleGameState state, AttackAction action)
    {
        var attackerPlayer = state.Player(action.Side);
        var defenderSide = RuleGameState.Other(action.Side);
        var defenderPlayer = state.Player(defenderSide);
        var targetMinion = defenderPlayer.Board.FirstOrDefault(minion => minion.EntityId == action.TargetEntityId);
        var targetsHero = defenderPlayer.Hero.EntityId == action.TargetEntityId;
        if (targetMinion is null && !targetsHero)
            return TransitionResult.Illegal(state, RuleError.InvalidTarget);
        if (targetMinion?.Stealth == true)
            return TransitionResult.Illegal(state, RuleError.StealthedTarget);
        if (defenderPlayer.Board.Any(minion => minion.Taunt && !minion.Stealth) && targetMinion?.Taunt != true)
            return TransitionResult.Illegal(state, RuleError.TauntBlocksTarget);

        var attacker = attackerPlayer.Board.FirstOrDefault(minion => minion.EntityId == action.SourceEntityId);
        if (attacker is not null)
            return AttackWithMinion(state, action, attacker, attackerPlayer, defenderSide, defenderPlayer, targetMinion, targetsHero);
        if (attackerPlayer.Hero.EntityId == action.SourceEntityId)
            return AttackWithHero(state, action, attackerPlayer, defenderSide, defenderPlayer, targetMinion, targetsHero);
        return TransitionResult.Illegal(state, RuleError.SourceNotFound);
    }

    private static TransitionResult AttackWithMinion(
        RuleGameState state,
        AttackAction action,
        MinionState attacker,
        PlayerState attackerPlayer,
        PlayerSide defenderSide,
        PlayerState defenderPlayer,
        MinionState? targetMinion,
        bool targetsHero)
    {
        if (attacker.Dormant)
            return TransitionResult.Illegal(state, RuleError.Dormant);
        if (attacker.Frozen)
            return TransitionResult.Illegal(state, RuleError.Frozen);
        if (attacker.AttacksThisTurn >= attacker.MaxAttacksThisTurn || attacker.Attack <= 0)
            return TransitionResult.Illegal(state, RuleError.Exhausted);
        if (attacker.SummonedThisTurn && !attacker.Charge && !attacker.Rush)
            return TransitionResult.Illegal(state, RuleError.Exhausted);
        if (attacker.SummonedThisTurn && attacker.Rush && !attacker.Charge && targetsHero)
            return TransitionResult.Illegal(state, RuleError.RushCannotAttackHero);

        var events = new List<RuleEvent>();
        var updatedAttacker = attacker with { AttacksThisTurn = attacker.AttacksThisTurn + 1 };
        if (targetsHero)
        {
            var damage = RuleDamage.Apply(defenderPlayer.Hero, attacker.Attack);
            defenderPlayer = defenderPlayer with { Hero = damage.Hero };
            events.Add(new RuleEvent("damage", attacker.EntityId, defenderPlayer.Hero.EntityId, damage.DamageApplied));
            if (attacker.Lifesteal)
                attackerPlayer = ApplyLifesteal(attackerPlayer, damage.DamageApplied, attacker.EntityId, events);
        }
        else
        {
            var target = targetMinion!;
            var damageToAttacker = RuleDamage.Apply(attacker, target.Attack, target.Poisonous);
            var damageToTarget = RuleDamage.Apply(target, attacker.Attack, attacker.Poisonous);
            updatedAttacker = damageToAttacker.Minion with { AttacksThisTurn = attacker.AttacksThisTurn + 1 };
            var updatedTarget = damageToTarget.Minion;
            defenderPlayer = defenderPlayer with
            {
                Board = Replace(defenderPlayer.Board, updatedTarget)
            };
            AddMinionDamageEvents(events, attacker.EntityId, target.EntityId, damageToTarget);
            AddMinionDamageEvents(events, target.EntityId, attacker.EntityId, damageToAttacker);
            if (attacker.Lifesteal)
                attackerPlayer = ApplyLifesteal(attackerPlayer, damageToTarget.DamageApplied, attacker.EntityId, events);
            if (target.Lifesteal)
                defenderPlayer = ApplyLifesteal(defenderPlayer, damageToAttacker.DamageApplied, target.EntityId, events);
        }

        attackerPlayer = attackerPlayer with { Board = Replace(attackerPlayer.Board, updatedAttacker) };
        var next = state.WithPlayer(action.Side, RemoveDead(attackerPlayer, events))
            .WithPlayer(defenderSide, RemoveDead(defenderPlayer, events));
        return TransitionResult.Legal(next, events);
    }

    private static TransitionResult AttackWithHero(
        RuleGameState state,
        AttackAction action,
        PlayerState attackerPlayer,
        PlayerSide defenderSide,
        PlayerState defenderPlayer,
        MinionState? targetMinion,
        bool targetsHero)
    {
        var hero = attackerPlayer.Hero;
        var attack = hero.Attack + (attackerPlayer.Weapon?.Attack ?? 0);
        if (hero.Frozen)
            return TransitionResult.Illegal(state, RuleError.Frozen);
        if (hero.AttacksThisTurn >= hero.MaxAttacksThisTurn || attack <= 0)
            return TransitionResult.Illegal(state, RuleError.Exhausted);

        var events = new List<RuleEvent>();
        var updatedHero = hero with { AttacksThisTurn = hero.AttacksThisTurn + 1 };
        if (targetsHero)
        {
            var damage = RuleDamage.Apply(defenderPlayer.Hero, attack);
            defenderPlayer = defenderPlayer with { Hero = damage.Hero };
            events.Add(new RuleEvent("damage", hero.EntityId, defenderPlayer.Hero.EntityId, damage.DamageApplied));
        }
        else
        {
            var target = targetMinion!;
            var damageToHero = RuleDamage.Apply(updatedHero, target.Attack);
            var damageToTarget = RuleDamage.Apply(target, attack);
            updatedHero = damageToHero.Hero;
            defenderPlayer = defenderPlayer with
            {
                Board = Replace(defenderPlayer.Board, damageToTarget.Minion)
            };
            AddMinionDamageEvents(events, hero.EntityId, target.EntityId, damageToTarget);
            events.Add(new RuleEvent("damage", target.EntityId, hero.EntityId, damageToHero.DamageApplied));
            if (target.Lifesteal)
                defenderPlayer = ApplyLifesteal(defenderPlayer, damageToHero.DamageApplied, target.EntityId, events);
        }

        var weapon = attackerPlayer.Weapon;
        if (weapon is not null)
        {
            weapon = weapon with { Durability = weapon.Durability - 1 };
            if (weapon.Durability <= 0)
            {
                events.Add(new RuleEvent("weapon_destroyed", weapon.EntityId, null, 0, weapon.CardId));
                weapon = null;
            }
        }
        attackerPlayer = attackerPlayer with { Hero = updatedHero, Weapon = weapon };
        var next = state.WithPlayer(action.Side, attackerPlayer)
            .WithPlayer(defenderSide, RemoveDead(defenderPlayer, events));
        return TransitionResult.Legal(next, events);
    }

    private static TransitionResult UseHeroPower(RuleGameState state, UseHeroPowerAction action)
    {
        var player = state.Player(action.Side);
        var power = player.HeroPower;
        if (!power.Available || power.UsesThisTurn >= power.MaxUsesThisTurn)
            return TransitionResult.Illegal(state, RuleError.Exhausted);
        if (power.Cost > player.Mana.Available)
            return TransitionResult.Illegal(state, RuleError.InsufficientMana);
        var targetError = ValidateTarget(state, action.Side, power.TargetKind, action.TargetEntityId);
        if (targetError != RuleError.None)
            return TransitionResult.Illegal(state, targetError);

        var nextPlayer = player with
        {
            Mana = player.Mana.Spend(power.Cost),
            HeroPower = power with { UsesThisTurn = power.UsesThisTurn + 1 }
        };
        return TransitionResult.Legal(
            state.WithPlayer(action.Side, nextPlayer),
            new[] { new RuleEvent("hero_power", power.EntityId, action.TargetEntityId) });
    }

    private static TransitionResult UseLocation(RuleGameState state, UseLocationAction action)
    {
        var player = state.Player(action.Side);
        var location = player.Locations.FirstOrDefault(candidate => candidate.EntityId == action.SourceEntityId);
        if (location is null)
            return TransitionResult.Illegal(state, RuleError.SourceNotFound);
        if (!location.Available || location.Cooldown > 0 || location.Durability <= 0)
            return TransitionResult.Illegal(state, RuleError.LocationUnavailable);
        if (!IsCharacter(state, action.SelectedEntityId))
            return TransitionResult.Illegal(state, RuleError.InvalidTarget);

        var updated = location with
        {
            Durability = location.Durability - 1,
            Cooldown = location.ActivationCooldown,
            Available = false
        };
        var locations = updated.Durability <= 0
            ? player.Locations.Remove(location)
            : player.Locations.Replace(location, updated);
        var nextPlayer = (player with { Locations = locations }).NormalizePositions();
        return TransitionResult.Legal(
            state.WithPlayer(action.Side, nextPlayer),
            new[] { new RuleEvent("use_location", location.EntityId, action.SelectedEntityId) });
    }

    private static TransitionResult EndTurn(RuleGameState state, EndTurnAction action)
    {
        var nextSide = RuleGameState.Other(action.Side);
        var nextPlayer = state.Player(nextSide);
        nextPlayer = nextPlayer with
        {
            Hero = nextPlayer.Hero with { AttacksThisTurn = 0 },
            HeroPower = nextPlayer.HeroPower with { UsesThisTurn = 0, Available = true },
            Board = nextPlayer.Board.Select(minion => minion with
            {
                AttacksThisTurn = 0,
                SummonedThisTurn = false,
                Frozen = false
            }).ToImmutableArray(),
            Locations = nextPlayer.Locations.Select(location =>
            {
                var cooldown = Math.Max(0, location.Cooldown - 1);
                return location with { Cooldown = cooldown, Available = cooldown == 0 };
            }).ToImmutableArray(),
            Mana = nextPlayer.Mana with
            {
                Available = Math.Max(0, nextPlayer.Mana.Maximum - nextPlayer.Mana.Locked),
                Temporary = 0,
                Spent = 0
            }
        };
        var next = state.WithPlayer(nextSide, nextPlayer) with
        {
            ActiveSide = nextSide,
            TurnNumber = action.Side == PlayerSide.Opponent ? state.TurnNumber + 1 : state.TurnNumber
        };
        return TransitionResult.Legal(next, new[] { new RuleEvent("end_turn") });
    }

    private static RuleError ValidateTarget(RuleGameState state, PlayerSide sourceSide, TargetKind targetKind, int? targetEntityId)
    {
        if (targetKind == TargetKind.None)
            return targetEntityId is null ? RuleError.None : RuleError.InvalidTarget;
        if (targetEntityId is null)
            return RuleError.TargetRequired;

        var friendly = state.Player(sourceSide);
        var enemy = state.Player(RuleGameState.Other(sourceSide));
        var friendlyMinion = friendly.Board.Any(minion => minion.EntityId == targetEntityId);
        var enemyMinion = enemy.Board.FirstOrDefault(minion => minion.EntityId == targetEntityId);
        var friendlyHero = friendly.Hero.EntityId == targetEntityId;
        var enemyHero = enemy.Hero.EntityId == targetEntityId;
        if (enemyMinion?.Stealth == true)
            return RuleError.StealthedTarget;

        return targetKind switch
        {
            TargetKind.AnyCharacter when friendlyMinion || enemyMinion is not null || friendlyHero || enemyHero => RuleError.None,
            TargetKind.EnemyCharacter when enemyMinion is not null || enemyHero => RuleError.None,
            TargetKind.FriendlyCharacter when friendlyMinion || friendlyHero => RuleError.None,
            TargetKind.EnemyMinion when enemyMinion is not null => RuleError.None,
            TargetKind.FriendlyMinion when friendlyMinion => RuleError.None,
            _ => RuleError.InvalidTarget
        };
    }

    private static bool IsCharacter(RuleGameState state, int entityId) =>
        state.Friendly.Hero.EntityId == entityId || state.Opponent.Hero.EntityId == entityId ||
        state.Friendly.Board.Any(minion => minion.EntityId == entityId) ||
        state.Opponent.Board.Any(minion => minion.EntityId == entityId);

    private static HeroState DamageHero(HeroState hero, int amount) => RuleDamage.Apply(hero, amount).Hero;

    private static PlayerState ApplyLifesteal(
        PlayerState player,
        int damageApplied,
        int sourceEntityId,
        ICollection<RuleEvent> events)
    {
        var healed = RuleDamage.Heal(player.Hero, damageApplied);
        var amount = healed.Health - player.Hero.Health;
        if (amount > 0)
            events.Add(new RuleEvent("heal", sourceEntityId, player.Hero.EntityId, amount));
        return player with { Hero = healed };
    }

    private static void AddMinionDamageEvents(
        ICollection<RuleEvent> events,
        int sourceEntityId,
        int targetEntityId,
        MinionDamageResult damage)
    {
        if (damage.DivineShieldLost)
            events.Add(new RuleEvent("divine_shield_lost", sourceEntityId, targetEntityId));
        events.Add(new RuleEvent("damage", sourceEntityId, targetEntityId, damage.DamageApplied));
    }

    private static ImmutableArray<MinionState> Replace(ImmutableArray<MinionState> board, MinionState minion)
    {
        var existing = board.First(candidate => candidate.EntityId == minion.EntityId);
        return board.Replace(existing, minion);
    }

    private static PlayerState RemoveDead(PlayerState player, ICollection<RuleEvent> events)
    {
        var dead = player.Board.Where(minion => minion.Health <= 0).ToArray();
        foreach (var minion in dead)
            events.Add(new RuleEvent("death", minion.EntityId, null, 0, minion.CardId));
        return (player with
        {
            Board = player.Board.Where(minion => minion.Health > 0).ToImmutableArray(),
            Graveyard = player.Graveyard.AddRange(dead.Select(minion => new ZoneCardState(minion.EntityId, minion.CardId)))
        }).NormalizePositions();
    }
}
