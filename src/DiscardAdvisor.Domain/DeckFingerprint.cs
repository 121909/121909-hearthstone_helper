using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DiscardAdvisor.Domain;

public sealed class DeckFingerprint
{
    private DeckFingerprint(IReadOnlyList<DeckCardCount> cards, int deckSize, string hash)
    {
        Cards = cards;
        DeckSize = deckSize;
        Hash = hash;
    }

    public IReadOnlyList<DeckCardCount> Cards { get; }

    public int DeckSize { get; }

    public string Hash { get; }

    public static bool TryCreate(IEnumerable<string?> cardIds, out DeckFingerprint? fingerprint)
    {
        if (cardIds is null)
            throw new ArgumentNullException(nameof(cardIds));

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var cardId in cardIds)
        {
            if (string.IsNullOrWhiteSpace(cardId))
            {
                fingerprint = null;
                return false;
            }

            counts[cardId!] = counts.TryGetValue(cardId!, out var count) ? count + 1 : 1;
        }

        return TryCreate(counts.Select(pair => new DeckCardCount(pair.Key, pair.Value)), out fingerprint);
    }

    public static bool TryCreate(IEnumerable<DeckCardCount> cardCounts, out DeckFingerprint? fingerprint)
    {
        if (cardCounts is null)
            throw new ArgumentNullException(nameof(cardCounts));

        var merged = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in cardCounts)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.CardId) || entry.Count <= 0)
            {
                fingerprint = null;
                return false;
            }

            merged[entry.CardId] = merged.TryGetValue(entry.CardId, out var count) ? count + entry.Count : entry.Count;
        }

        var normalized = merged
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new DeckCardCount(pair.Key, pair.Value))
            .ToArray();
        var canonical = string.Concat(normalized.Select(entry => $"{entry.CardId}:{entry.Count}\n"));

        using var sha256 = SHA256.Create();
        var hash = ToLowerHex(sha256.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
        fingerprint = new DeckFingerprint(normalized, normalized.Sum(entry => entry.Count), hash);
        return true;
    }

    private static string ToLowerHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var value in bytes)
            builder.Append(value.ToString("x2"));
        return builder.ToString();
    }
}

