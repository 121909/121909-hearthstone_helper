using System;
using System.Linq;

namespace DiscardAdvisor.Domain.Snapshots;

public sealed class GameSnapshotBuilder
{
    public GameSnapshot Build(GameObservation observation)
    {
        if (observation is null)
            throw new ArgumentNullException(nameof(observation));

        var publicGraveyard = observation.Opponent.Graveyard
            .Where(card => card.IsPublic && !string.IsNullOrWhiteSpace(card.CardId))
            .Select(card => new ZoneCardSnapshot(card.EntityId, card.CardId!, card.CreatedByEntityId));
        var revealedCards = observation.Opponent.RevealedCards
            .Where(card => card.IsPublic && !string.IsNullOrWhiteSpace(card.CardId))
            .GroupBy(card => card.CardId!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new DeckEntrySnapshot(group.Key, group.Count()));
        var opponent = new OpponentPlayerSnapshot(
            observation.Opponent.Hero,
            observation.Opponent.HeroPower,
            observation.Opponent.Hand.Count,
            observation.Opponent.Board.OrderBy(entity => entity.BoardPosition),
            observation.Opponent.Locations.OrderBy(entity => entity.BoardPosition),
            observation.Opponent.DeckCount,
            observation.Opponent.Fatigue,
            publicGraveyard.OrderBy(card => card.EntityId),
            revealedCards,
            observation.Opponent.SecretCount,
            observation.Opponent.SecretCandidates.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal),
            observation.Opponent.Weapon,
            observation.Opponent.QuestCardId);

        var friendly = NormalizeFriendly(observation.Friendly);
        var derived = new DerivedStateSnapshot(
            observation.Derived.PlatysaurBindings.OrderBy(binding => binding.PlatysaurEntityId),
            observation.Derived.TemporaryEntityIds.Distinct().OrderBy(entityId => entityId),
            observation.Derived.ShredsOfTimeInDeck,
            observation.Derived.UnsupportedInteractions.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal));
        var snapshot = new GameSnapshot(
            TargetDeckProfile.RuleSetVersion,
            observation.HearthstoneBuild,
            observation.HdtVersion,
            observation.CardDefsHash,
            observation.GameId,
            "pending",
            observation.TurnNumber,
            observation.Step,
            observation.ActivePlayer,
            observation.RemainingTurnTimeMs,
            observation.IsStable,
            friendly,
            opponent,
            observation.ActionsThisTurn,
            derived,
            NormalizeChoice(observation.CurrentChoice));
        return snapshot.WithStateId(SnapshotStateId.Calculate(snapshot));
    }

    private static FriendlyPlayerSnapshot NormalizeFriendly(FriendlyPlayerSnapshot friendly) => new(
        friendly.Hero,
        friendly.HeroPower,
        friendly.Mana,
        friendly.Hand.OrderBy(card => card.ZonePosition),
        friendly.Board.OrderBy(entity => entity.BoardPosition),
        friendly.Locations.OrderBy(entity => entity.BoardPosition),
        friendly.OriginalDeck.OrderBy(card => card.CardId, StringComparer.Ordinal),
        friendly.KnownRemainingDeck.OrderBy(card => card.CardId, StringComparer.Ordinal),
        friendly.DeckCount,
        friendly.Fatigue,
        friendly.Graveyard.OrderBy(card => card.EntityId),
        friendly.Discarded.OrderBy(card => card.EntityId),
        friendly.DiscardCount,
        friendly.Weapon);

    private static ChoiceSnapshot? NormalizeChoice(ChoiceSnapshot? choice) => choice is null
        ? null
        : new ChoiceSnapshot(
            choice.ChoiceId,
            choice.ChoiceType,
            choice.Candidates.OrderBy(candidate => candidate.EntityId),
            choice.SourceEntityId);
}
