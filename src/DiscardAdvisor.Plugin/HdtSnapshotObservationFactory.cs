using System;
using System.Collections.Generic;
using System.Linq;
using DiscardAdvisor.Domain;
using DiscardAdvisor.Domain.Snapshots;
using DiscardAdvisor.Rules;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using HdtApiCore = Hearthstone_Deck_Tracker.API.Core;

namespace DiscardAdvisor.Plugin;

internal sealed class HdtSnapshotObservationFactory : ISnapshotObservationSource
{
    private readonly SpecialMechanicsTracker _mechanics;

    public HdtSnapshotObservationFactory(SpecialMechanicsTracker mechanics)
    {
        _mechanics = mechanics;
    }

    public bool TryCapture(Guid gameId, bool isStable, out GameObservation? observation)
    {
        var game = HdtApiCore.Game;
        var friendlyHero = game.Player.Hero;
        var opponentHero = game.Opponent.Hero;
        var friendlyHeroPower = FindHeroPower(game.Player);
        var opponentHeroPower = FindHeroPower(game.Opponent);
        var turnNumber = game.GetTurnNumber();
        var gameEntity = game.GameEntity;

        if (friendlyHero is null || opponentHero is null || friendlyHeroPower is null || opponentHeroPower is null ||
            gameEntity is null || turnNumber < 1 || !HasPublicCard(friendlyHero) || !HasPublicCard(opponentHero) ||
            !HasPublicCard(friendlyHeroPower) || !HasPublicCard(opponentHeroPower))
        {
            observation = null;
            return false;
        }

        var activePlayer = game.PlayerEntity?.HasTag(GameTag.CURRENT_PLAYER) == true
            ? "FRIENDLY"
            : game.OpponentEntity?.HasTag(GameTag.CURRENT_PLAYER) == true ? "OPPONENT" : "NONE";
        var compatibility = HdtGameContextProvider.CaptureCompatibility();
        var remainingTurnTimeMs = CaptureRemainingTurnTimeMs(game);
        foreach (var location in game.Player.Board.Where(entity => entity.IsLocation && HasPublicCard(entity)))
        {
            var captured = CaptureLocation(location);
            _mechanics.RecordLocation(captured.EntityId, captured.Durability, captured.Cooldown, captured.Available);
        }
        var mechanics = _mechanics.Capture(
            game.Player.Hand.Select(entity => entity.Id),
            game.Player.Minions.Where(entity => entity.CardId == DiscardWarlockCardIds.Platysaur).Select(entity => entity.Id),
            game.Player.EntitiesDiscardedFromHand.Count,
            game.Player.Deck.Count(entity => entity.CardId == DiscardWarlockCardIds.ShredOfTime));
        var friendly = CaptureFriendly(game, friendlyHero, friendlyHeroPower, mechanics);
        var opponent = CaptureOpponent(game, opponentHero, opponentHeroPower);
        var sensitive = new SensitiveGameMetadata(null, null, null, null);

        observation = new GameObservation(
            compatibility.HearthstoneBuild,
            compatibility.HdtVersion,
            compatibility.CardDefsSha256,
            gameId,
            turnNumber,
            ((Step)gameEntity.GetTag(GameTag.STEP)).ToString(),
            activePlayer,
            Math.Max(0, remainingTurnTimeMs),
            isStable,
            friendly,
            opponent,
            new DerivedStateSnapshot(
                mechanics.PlatysaurBindings.Select(binding => new PlatysaurBindingSnapshot(binding.Key, binding.Value)),
                mechanics.TemporaryEntityIds,
                mechanics.ShredsOfTimeInDeck,
                Array.Empty<string>()),
            sensitive);
        return true;
    }

    private static FriendlyPlayerSnapshot CaptureFriendly(
        GameV2 game,
        Entity hero,
        Entity heroPower,
        SpecialMechanicsState mechanics)
    {
        var playerEntity = game.PlayerEntity!;
        var originalDeck = DeckList.Instance.ActiveDeckVersion?.Cards
            .Where(card => !string.IsNullOrWhiteSpace(card.Id) && card.Count > 0)
            .GroupBy(card => card.Id, StringComparer.Ordinal)
            .Select(group => new DeckEntrySnapshot(group.Key, group.Sum(card => card.Count)))
            .OrderBy(card => card.CardId, StringComparer.Ordinal)
            ?? Enumerable.Empty<DeckEntrySnapshot>();
        var remainingDeck = game.Player.KnownCardsInDeck
            .Where(card => !string.IsNullOrWhiteSpace(card.Id) && card.Count > 0)
            .GroupBy(card => card.Id, StringComparer.Ordinal)
            .Select(group => new DeckEntrySnapshot(group.Key, group.Sum(card => card.Count)))
            .OrderBy(card => card.CardId, StringComparer.Ordinal);

        return new FriendlyPlayerSnapshot(
            CaptureHero(hero),
            CaptureHeroPower(heroPower),
            CaptureMana(playerEntity),
            game.Player.Hand.Where(HasPublicCard).OrderBy(entity => entity.ZonePosition)
                .Select(entity => CaptureHandCard(entity, mechanics.TemporaryEntityIds.Contains(entity.Id))),
            game.Player.Minions.Where(HasPublicCard).OrderBy(entity => entity.ZonePosition).Select(CaptureMinion),
            game.Player.Board.Where(entity => entity.IsLocation && HasPublicCard(entity)).OrderBy(entity => entity.ZonePosition).Select(CaptureLocation),
            originalDeck,
            remainingDeck,
            game.Player.DeckCount,
            game.Player.Fatigue,
            game.Player.Graveyard.Where(HasPublicCard).Select(CaptureZoneCard),
            game.Player.EntitiesDiscardedFromHand.Where(HasPublicCard).Select(CaptureZoneCard),
            mechanics.DiscardCount,
            CaptureWeapon(game.Player));
    }

    private static OpponentObservation CaptureOpponent(GameV2 game, Entity hero, Entity heroPower)
    {
        var revealed = game.Opponent.RevealedEntities
            .Select(entity => CaptureObservedCard(entity, !entity.Info.Hidden && HasPublicCard(entity)));

        return new OpponentObservation(
            CaptureHero(hero),
            CaptureHeroPower(heroPower),
            game.Opponent.Hand.Select(entity => CaptureObservedCard(entity, false)),
            game.Opponent.Minions.Where(HasPublicCard).OrderBy(entity => entity.ZonePosition).Select(CaptureMinion),
            game.Opponent.Board.Where(entity => entity.IsLocation && HasPublicCard(entity)).OrderBy(entity => entity.ZonePosition).Select(CaptureLocation),
            game.Opponent.DeckCount,
            game.Opponent.Fatigue,
            game.Opponent.Graveyard.Select(entity => CaptureObservedCard(entity, HasPublicCard(entity))),
            revealed,
            game.Opponent.Secrets.Count(),
            Array.Empty<string>(),
            CaptureWeapon(game.Opponent),
            game.Opponent.Quests.FirstOrDefault(HasPublicCard)?.CardId);
    }

    private static HeroSnapshot CaptureHero(Entity entity) => new(
        entity.Id,
        entity.CardId!,
        Math.Max(0, entity.Health),
        Math.Max(1, entity.GetTag(GameTag.HEALTH)),
        Math.Max(0, entity.GetTag(GameTag.ARMOR)),
        Math.Max(0, entity.Attack),
        entity.HasTag(GameTag.FROZEN),
        entity.HasTag(GameTag.IMMUNE),
        Math.Max(0, entity.GetTag(GameTag.NUM_ATTACKS_THIS_TURN)),
        GetMaxAttacks(entity));

    private static WeaponSnapshot? CaptureWeapon(Player player)
    {
        var weapon = player.Board.FirstOrDefault(entity => entity.IsWeapon && HasPublicCard(entity));
        if (weapon is null)
            return null;

        var durability = weapon.HasTag(GameTag.HEALTH)
            ? Math.Max(0, weapon.Health)
            : Math.Max(0, weapon.GetTag(GameTag.DURABILITY));
        return new WeaponSnapshot(weapon.Id, weapon.CardId!, Math.Max(0, weapon.Attack), durability);
    }

    private static HeroPowerSnapshot CaptureHeroPower(Entity entity)
    {
        var exhausted = entity.HasTag(GameTag.EXHAUSTED);
        return new HeroPowerSnapshot(entity.Id, entity.CardId!, Math.Max(0, entity.Cost), !exhausted, exhausted ? 1 : 0, 1);
    }

    private static ManaSnapshot CaptureMana(Entity playerEntity)
    {
        var maximum = Math.Max(0, playerEntity.GetTag(GameTag.RESOURCES));
        var temporary = Math.Max(0, playerEntity.GetTag(GameTag.TEMP_RESOURCES));
        var spent = Math.Max(0, playerEntity.GetTag(GameTag.RESOURCES_USED));
        return new ManaSnapshot(
            Math.Max(0, maximum + temporary - spent),
            temporary,
            spent,
            maximum,
            Math.Max(0, playerEntity.GetTag(GameTag.OVERLOAD_LOCKED)),
            Math.Max(0, playerEntity.GetTag(GameTag.OVERLOAD_OWED)));
    }

    private static HandCardSnapshot CaptureHandCard(Entity entity, bool temporary)
    {
        return new HandCardSnapshot(
            entity.Id,
            entity.CardId!,
            entity.ZonePosition,
            Math.Max(0, entity.Cost),
            temporary,
            PositiveOrNull(entity.GetTag(GameTag.CREATOR)),
            temporary ? HdtApiCore.Game.GetTurnNumber() : null);
    }

    private static MinionSnapshot CaptureMinion(Entity entity)
    {
        var summonedThisTurn = entity.GetTag(GameTag.NUM_TURNS_IN_PLAY) == 0;
        var attacksThisTurn = Math.Max(0, entity.GetTag(GameTag.NUM_ATTACKS_THIS_TURN));
        var maxAttacks = GetMaxAttacks(entity);
        var dormant = entity.HasTag(GameTag.DORMANT);
        var frozen = entity.HasTag(GameTag.FROZEN);
        var charge = entity.HasTag(GameTag.CHARGE);
        var rush = entity.HasTag(GameTag.RUSH);
        var canAttack = entity.Attack > 0 && !dormant && !frozen && !entity.HasTag(GameTag.EXHAUSTED) &&
            attacksThisTurn < maxAttacks && (!summonedThisTurn || charge || rush);

        return new MinionSnapshot(
            entity.Id,
            entity.CardId!,
            entity.ZonePosition,
            Math.Max(0, entity.Attack),
            Math.Max(0, entity.Health),
            Math.Max(1, entity.GetTag(GameTag.HEALTH)),
            attacksThisTurn,
            maxAttacks,
            frozen,
            dormant,
            entity.HasTag(GameTag.TAUNT),
            rush,
            charge,
            entity.HasTag(GameTag.STEALTH),
            entity.HasTag(GameTag.DIVINE_SHIELD),
            entity.HasTag(GameTag.POISONOUS),
            entity.HasTag(GameTag.LIFESTEAL),
            entity.HasTag(GameTag.REBORN),
            entity.HasTag(GameTag.IMMUNE),
            entity.HasTag(GameTag.SILENCED),
            summonedThisTurn,
            canAttack);
    }

    private static LocationSnapshot CaptureLocation(Entity entity)
    {
        var durability = entity.HasTag(GameTag.HEALTH)
            ? Math.Max(0, entity.Health)
            : Math.Max(0, entity.GetTag(GameTag.DURABILITY));
        var cooldown = Math.Max(0, entity.GetTag(GameTag.LOCATION_ACTION_COOLDOWN));
        return new LocationSnapshot(entity.Id, entity.CardId!, entity.ZonePosition, durability, cooldown, cooldown == 0);
    }

    private static ZoneCardSnapshot CaptureZoneCard(Entity entity) =>
        new(entity.Id, entity.CardId!, PositiveOrNull(entity.GetTag(GameTag.CREATOR)));

    private static ObservedCard CaptureObservedCard(Entity entity, bool isPublic) =>
        new(entity.Id, entity.CardId, isPublic, PositiveOrNull(entity.GetTag(GameTag.CREATOR)));

    private static Entity? FindHeroPower(Player player) =>
        player.PlayerEntities.FirstOrDefault(entity => entity.IsHeroPower && entity.IsInPlay);

    private static int GetMaxAttacks(Entity entity) =>
        entity.HasTag(GameTag.MEGA_WINDFURY) ? 4 : entity.HasTag(GameTag.WINDFURY) ? 2 : 1;

    private static int? PositiveOrNull(int value) => value > 0 ? value : null;

    private static int CaptureRemainingTurnTimeMs(GameV2 game)
    {
        var activeEntity = game.PlayerEntity?.HasTag(GameTag.CURRENT_PLAYER) == true
            ? game.PlayerEntity
            : game.OpponentEntity?.HasTag(GameTag.CURRENT_PLAYER) == true ? game.OpponentEntity : null;
        return Math.Max(0, activeEntity?.GetTag(GameTag.TIMEOUT) ?? 0) * 1000;
    }

    private static bool HasPublicCard(Entity entity) => entity.Id > 0 && !string.IsNullOrWhiteSpace(entity.CardId);
}
