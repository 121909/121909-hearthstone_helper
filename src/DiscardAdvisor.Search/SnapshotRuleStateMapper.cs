using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using DiscardAdvisor.Domain.Snapshots;
using DiscardAdvisor.Rules;
using DiscardAdvisor.Rules.Model;

namespace DiscardAdvisor.Search;

public sealed record RuleStateMappingResult(
    bool IsSupported,
    RuleGameState? State,
    ImmutableArray<string> UnsupportedInteractions);

public sealed class SnapshotRuleStateMapper
{
    public RuleStateMappingResult Map(GameSnapshot snapshot)
    {
        if (snapshot is null)
            throw new ArgumentNullException(nameof(snapshot));
        var unsupported = ImmutableArray.CreateBuilder<string>();
        unsupported.AddRange(snapshot.Derived.UnsupportedInteractions
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        var nextEntityId = MaximumEntityId(snapshot) + 1;
        var friendlyHand = snapshot.Friendly.Hand.Select(card =>
        {
            if (!DiscardWarlockCardCatalog.TryCreate(
                    card.CardId,
                    card.EntityId,
                    out var mapped,
                    card.DynamicCost,
                    snapshot.Friendly.DiscardCount) || mapped is null)
            {
                unsupported.Add($"unknown_hand_card:{card.CardId}");
                return null;
            }
            return mapped with { Temporary = card.Temporary };
        }).Where(card => card is not null).Cast<HandCardState>().ToImmutableArray();
        var deck = ImmutableArray.CreateBuilder<HandCardState>();
        foreach (var entry in snapshot.Friendly.KnownRemainingDeck.OrderBy(entry => entry.CardId, StringComparer.Ordinal))
        {
            for (var index = 0; index < entry.Count; index++)
            {
                if (!DiscardWarlockCardCatalog.TryCreate(
                        entry.CardId,
                        nextEntityId++,
                        out var mapped,
                        discardCount: snapshot.Friendly.DiscardCount) || mapped is null)
                {
                    unsupported.Add($"unknown_deck_card:{entry.CardId}");
                    continue;
                }
                deck.Add(mapped);
            }
        }
        var knownShreds = deck.Count(card => card.CardId == DiscardWarlockCardIds.ShredOfTime);
        for (var index = knownShreds; index < snapshot.Derived.ShredsOfTimeInDeck; index++)
            deck.Add(DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.ShredOfTime, nextEntityId++));
        if (deck.Count != snapshot.Friendly.DeckCount)
            unsupported.Add($"incomplete_known_deck:{deck.Count}/{snapshot.Friendly.DeckCount}");
        var pendingChoice = MapChoice(snapshot, unsupported);
        AddUnsupportedReborn(snapshot.Friendly.Board, unsupported);
        AddUnsupportedReborn(snapshot.Opponent.Board, unsupported);

        if (unsupported.Count > 0)
            return new RuleStateMappingResult(false, null, unsupported.ToImmutable());

        var friendly = PlayerState.Create(
            MapHero(snapshot.Friendly.Hero),
            MapHeroPower(snapshot.Friendly.HeroPower),
            MapMana(snapshot.Friendly.Mana),
            friendlyHand,
            snapshot.Friendly.Board.Select(MapMinion),
            snapshot.Friendly.Locations.Select(MapLocation),
            deck,
            snapshot.Friendly.Graveyard.Select(card => new ZoneCardState(card.EntityId, card.CardId)),
            snapshot.Friendly.Fatigue,
            snapshot.Friendly.Weapon is null ? null : MapWeapon(snapshot.Friendly.Weapon),
            snapshot.Friendly.DiscardCount,
            deckOrderKnown: false);
        var opponent = PlayerState.Create(
            MapHero(snapshot.Opponent.Hero),
            MapHeroPower(snapshot.Opponent.HeroPower),
            new ManaState(0, 0, 0, 0, 0, 0),
            board: snapshot.Opponent.Board.Select(MapMinion),
            locations: snapshot.Opponent.Locations.Select(MapLocation),
            graveyard: snapshot.Opponent.Graveyard.Select(card => new ZoneCardState(card.EntityId, card.CardId)),
            fatigue: snapshot.Opponent.Fatigue,
            weapon: snapshot.Opponent.Weapon is null ? null : MapWeapon(snapshot.Opponent.Weapon));
        var side = snapshot.ActivePlayer switch
        {
            "FRIENDLY" => PlayerSide.Friendly,
            "OPPONENT" => PlayerSide.Opponent,
            _ => (PlayerSide?)null
        };
        if (side is null)
            return new RuleStateMappingResult(false, null, ImmutableArray.Create("no_active_player"));

        var state = new RuleGameState(
            snapshot.TurnNumber,
            side.Value,
            friendly,
            opponent,
            nextEntityId,
            pendingChoice,
            snapshot.Derived.PlatysaurBindings.ToImmutableDictionary(
                binding => binding.PlatysaurEntityId,
                binding => binding.DrawnEntityId));
        return new RuleStateMappingResult(true, state, ImmutableArray<string>.Empty);
    }

    private static PendingChoiceState? MapChoice(
        GameSnapshot snapshot,
        ImmutableArray<string>.Builder unsupported)
    {
        if (snapshot.CurrentChoice is null)
            return null;
        var sourceCardId = snapshot.CurrentChoice.SourceEntityId is int sourceEntityId
            ? FindCardId(snapshot, sourceEntityId)
            : null;
        if (string.IsNullOrWhiteSpace(sourceCardId))
        {
            unsupported.Add("unknown_choice_source");
            return null;
        }
        var candidates = snapshot.CurrentChoice.Candidates.Select(candidate =>
        {
            var cardId = candidate.CardId ?? FindCardId(snapshot, candidate.EntityId);
            if (string.IsNullOrWhiteSpace(cardId))
            {
                unsupported.Add($"unknown_choice_candidate:{candidate.EntityId}");
                cardId = string.Empty;
            }
            else if (sourceCardId == DiscardWarlockCardIds.CursedCatacombs &&
                     !DiscardWarlockCardCatalog.TryCreate(cardId!, candidate.EntityId, out _))
            {
                unsupported.Add($"unknown_choice_card:{cardId}");
            }
            return new ChoiceCandidateState(candidate.EntityId, cardId ?? string.Empty);
        });
        return new PendingChoiceState(
            snapshot.CurrentChoice.ChoiceId,
            snapshot.CurrentChoice.ChoiceType,
            sourceCardId!,
            candidates.ToImmutableArray(),
            snapshot.CurrentChoice.SourceEntityId);
    }

    private static string? FindCardId(GameSnapshot snapshot, int entityId) =>
        snapshot.Friendly.Hand.FirstOrDefault(card => card.EntityId == entityId)?.CardId ??
        snapshot.Friendly.Board.FirstOrDefault(card => card.EntityId == entityId)?.CardId ??
        snapshot.Friendly.Locations.FirstOrDefault(card => card.EntityId == entityId)?.CardId ??
        snapshot.Friendly.Graveyard.FirstOrDefault(card => card.EntityId == entityId)?.CardId ??
        snapshot.Friendly.Discarded.FirstOrDefault(card => card.EntityId == entityId)?.CardId;

    private static HeroState MapHero(HeroSnapshot hero) => new(
        hero.EntityId,
        hero.CardId,
        hero.Health,
        hero.MaxHealth,
        hero.Armor,
        hero.Attack,
        hero.Frozen,
        hero.Immune,
        hero.AttacksThisTurn,
        hero.MaxAttacksThisTurn);

    private static HeroPowerState MapHeroPower(HeroPowerSnapshot power) => new(
        power.EntityId,
        power.CardId,
        power.Cost,
        power.UsesThisTurn,
        power.MaxUsesThisTurn,
        TargetKind.None,
        power.Available);

    private static ManaState MapMana(ManaSnapshot mana) => new(
        mana.Available,
        mana.Temporary,
        mana.Spent,
        mana.Maximum,
        mana.Locked,
        mana.OverloadedNextTurn);

    private static WeaponState MapWeapon(WeaponSnapshot weapon) => new(
        weapon.EntityId,
        weapon.CardId,
        weapon.Attack,
        weapon.Durability);

    private static MinionState MapMinion(MinionSnapshot minion) => new(
        minion.EntityId,
        minion.CardId,
        minion.BoardPosition,
        minion.Attack,
        minion.Health,
        minion.MaxHealth,
        minion.AttacksThisTurn,
        minion.MaxAttacksThisTurn,
        minion.Frozen,
        minion.Dormant,
        minion.Taunt,
        minion.Rush,
        minion.Charge,
        minion.Stealth,
        minion.Immune,
        minion.SummonedThisTurn,
        minion.DivineShield,
        minion.Poisonous,
        minion.Lifesteal);

    private static void AddUnsupportedReborn(
        IEnumerable<MinionSnapshot> board,
        ImmutableArray<string>.Builder unsupported)
    {
        foreach (var minion in board.Where(minion => minion.Reborn))
            unsupported.Add($"unsupported_reborn:{minion.EntityId}:{minion.CardId}");
    }

    private static LocationState MapLocation(LocationSnapshot location) => new(
        location.EntityId,
        location.CardId,
        location.BoardPosition,
        location.Durability,
        location.Cooldown,
        2,
        location.Available);

    private static int MaximumEntityId(GameSnapshot snapshot)
    {
        var entityIds = snapshot.Friendly.Hand.Select(card => card.EntityId)
            .Concat(snapshot.Friendly.Board.Select(card => card.EntityId))
            .Concat(snapshot.Friendly.Locations.Select(card => card.EntityId))
            .Concat(snapshot.Friendly.Graveyard.Select(card => card.EntityId))
            .Concat(snapshot.Opponent.Board.Select(card => card.EntityId))
            .Concat(snapshot.Opponent.Locations.Select(card => card.EntityId))
            .Concat(new[]
            {
                snapshot.Friendly.Hero.EntityId,
                snapshot.Friendly.HeroPower.EntityId,
                snapshot.Friendly.Weapon?.EntityId ?? 0,
                snapshot.Opponent.Hero.EntityId,
                snapshot.Opponent.HeroPower.EntityId,
                snapshot.Opponent.Weapon?.EntityId ?? 0
            });
        return Math.Max(1000, entityIds.Max());
    }
}
