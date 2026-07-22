using System;

namespace DiscardAdvisor.Domain;

public sealed class DeckCardCount : IEquatable<DeckCardCount>
{
    public DeckCardCount(string cardId, int count)
    {
        if (string.IsNullOrWhiteSpace(cardId))
            throw new ArgumentException("A card id is required.", nameof(cardId));
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count), "A deck card count must be positive.");

        CardId = cardId;
        Count = count;
    }

    public string CardId { get; }

    public int Count { get; }

    public bool Equals(DeckCardCount? other) =>
        other is not null && string.Equals(CardId, other.CardId, StringComparison.Ordinal) && Count == other.Count;

    public override bool Equals(object? obj) => Equals(obj as DeckCardCount);

    public override int GetHashCode() => (CardId.GetHashCode() * 397) ^ Count;
}

