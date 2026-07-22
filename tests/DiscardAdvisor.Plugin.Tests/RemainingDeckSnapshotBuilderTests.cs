using System.Linq;
using DiscardAdvisor.Domain.Snapshots;
using Xunit;

namespace DiscardAdvisor.Plugin.Tests;

public sealed class RemainingDeckSnapshotBuilderTests
{
    [Fact]
    public void ReconstructsUnorderedRemainingDeckWithoutInventingUnknownRemovals()
    {
        var original = new[]
        {
            new DeckEntrySnapshot("CARD_A", 2),
            new DeckEntrySnapshot("CARD_B", 1)
        };

        var remaining = RemainingDeckSnapshotBuilder.Build(
            original,
            new[] { "CARD_A", "UNKNOWN", "CARD_A", "CARD_A" },
            new[] { "CREATED", "CREATED" });

        Assert.Equal(
            new[] { ("CARD_B", 1), ("CREATED", 2) },
            remaining.Select(entry => (entry.CardId, entry.Count)));
    }
}
