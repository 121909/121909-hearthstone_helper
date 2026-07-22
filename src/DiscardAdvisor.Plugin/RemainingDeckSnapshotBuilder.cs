using System;
using System.Collections.Generic;
using System.Linq;
using DiscardAdvisor.Domain.Snapshots;

namespace DiscardAdvisor.Plugin;

internal static class RemainingDeckSnapshotBuilder
{
    public static IReadOnlyList<DeckEntrySnapshot> Build(
        IEnumerable<DeckEntrySnapshot> originalDeck,
        IEnumerable<string> removedOriginalCardIds,
        IEnumerable<string> createdCardsInDeck)
    {
        if (originalDeck is null)
            throw new ArgumentNullException(nameof(originalDeck));
        if (removedOriginalCardIds is null)
            throw new ArgumentNullException(nameof(removedOriginalCardIds));
        if (createdCardsInDeck is null)
            throw new ArgumentNullException(nameof(createdCardsInDeck));

        var remaining = originalDeck
            .Where(entry => entry.Count > 0)
            .GroupBy(entry => entry.CardId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Count), StringComparer.Ordinal);
        foreach (var cardId in removedOriginalCardIds.Where(cardId => !string.IsNullOrWhiteSpace(cardId)))
        {
            if (remaining.TryGetValue(cardId, out var count) && count > 0)
                remaining[cardId] = count - 1;
        }
        foreach (var cardId in createdCardsInDeck.Where(cardId => !string.IsNullOrWhiteSpace(cardId)))
            remaining[cardId] = remaining.TryGetValue(cardId, out var count) ? count + 1 : 1;

        return remaining.Where(entry => entry.Value > 0)
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new DeckEntrySnapshot(entry.Key, entry.Value))
            .ToArray();
    }
}
