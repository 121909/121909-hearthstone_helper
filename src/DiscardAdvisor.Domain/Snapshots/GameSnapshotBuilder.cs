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
            observation.Opponent.Board,
            observation.Opponent.Locations,
            observation.Opponent.DeckCount,
            observation.Opponent.Fatigue,
            publicGraveyard,
            revealedCards,
            observation.Opponent.SecretCount,
            observation.Opponent.SecretCandidates.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal),
            observation.Opponent.Weapon,
            observation.Opponent.QuestCardId);

        return new GameSnapshot(
            TargetDeckProfile.RuleSetVersion,
            observation.HearthstoneBuild,
            observation.HdtVersion,
            observation.CardDefsHash,
            observation.GameId,
            observation.StateId,
            observation.TurnNumber,
            observation.Step,
            observation.ActivePlayer,
            observation.RemainingTurnTimeMs,
            observation.IsStable,
            observation.Friendly,
            opponent,
            observation.ActionsThisTurn,
            observation.Derived,
            observation.CurrentChoice);
    }
}
