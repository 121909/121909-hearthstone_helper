using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiscardAdvisor.Domain;
using DiscardAdvisor.Domain.Snapshots;
using Json.Schema;
using Xunit;

namespace DiscardAdvisor.Plugin.Tests;

public sealed class GameSnapshotBuilderTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Fact]
    public void CopiesCollectionsAndCannotObserveLaterSourceMutations()
    {
        var hand = new List<HandCardSnapshot> { new(101, "EX1_308", 1, 1, false) };
        var friendly = CreateFriendly(hand);
        var observation = CreateObservation(friendly);

        var snapshot = new GameSnapshotBuilder().Build(observation);
        hand.Add(new HandCardSnapshot(102, "BT_300", 2, 6, false));

        Assert.Single(snapshot.Friendly.Hand);
        Assert.IsAssignableFrom<IReadOnlyList<HandCardSnapshot>>(snapshot.Friendly.Hand);
        Assert.Throws<NotSupportedException>(() => ((IList<HandCardSnapshot>)snapshot.Friendly.Hand).Add(hand[1]));
    }

    [Fact]
    public void RemovesHiddenOpponentAndSensitiveMetadata()
    {
        var snapshot = new GameSnapshotBuilder().Build(CreateObservation(CreateFriendly(Array.Empty<HandCardSnapshot>())));
        var json = JsonSerializer.Serialize(snapshot, SerializerOptions);

        Assert.Equal(2, snapshot.Opponent.HandCount);
        Assert.DoesNotContain("HIDDEN_HAND_CARD", json, StringComparison.Ordinal);
        Assert.DoesNotContain("HIDDEN_REVEALED_CARD", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Player#1234", json, StringComparison.Ordinal);
        Assert.DoesNotContain("account-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("server-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Users\\secret", json, StringComparison.Ordinal);
        Assert.Equal(new[] { "PUBLIC_CARD" }, snapshot.Opponent.RevealedCards.Select(card => card.CardId));
    }

    [Fact]
    public void ProducesJsonThatMatchesSnapshotSchema()
    {
        var snapshot = new GameSnapshotBuilder().Build(CreateObservation(CreateFriendly(Array.Empty<HandCardSnapshot>())));
        var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
        var options = new EvaluationOptions { OutputFormat = OutputFormat.List };
        var buildOptions = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
        JsonSchema.FromText(File.ReadAllText(Path.Combine("schemas", "common.schema.json")), buildOptions);
        JsonSchema.FromText(File.ReadAllText(Path.Combine("schemas", "action.schema.json")), buildOptions);
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine("schemas", "snapshot.schema.json")), buildOptions);

        var result = schema.Evaluate(JsonDocument.Parse(json).RootElement, options);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void StateIdIsStableAcrossEquivalentInputOrder()
    {
        var firstHand = new[]
        {
            new HandCardSnapshot(102, "BT_300", 2, 6, false),
            new HandCardSnapshot(101, "EX1_308", 1, 1, false)
        };
        var secondHand = firstHand.Reverse();

        var first = new GameSnapshotBuilder().Build(CreateObservation(CreateFriendly(firstHand)));
        var second = new GameSnapshotBuilder().Build(CreateObservation(CreateFriendly(secondHand)));

        Assert.Equal(first.StateId, second.StateId);
        Assert.StartsWith("turn-3:", first.StateId, StringComparison.Ordinal);
    }

    [Fact]
    public void StateIdChangesWhenVisibleStateChanges()
    {
        var first = new GameSnapshotBuilder().Build(CreateObservation(CreateFriendly(Array.Empty<HandCardSnapshot>(), 3)));
        var second = new GameSnapshotBuilder().Build(CreateObservation(CreateFriendly(Array.Empty<HandCardSnapshot>(), 2)));

        Assert.NotEqual(first.StateId, second.StateId);
    }

    internal static FriendlyPlayerSnapshot CreateFriendly(IEnumerable<HandCardSnapshot> hand, int availableMana = 3) => new(
        new HeroSnapshot(1, "HERO_07", 30, 30, 0, 0, false, false, 0, 1),
        new HeroPowerSnapshot(2, "CS2_056", 2, true, 0, 1),
        new ManaSnapshot(availableMana, 0, 3 - availableMana, 3, 0, 0),
        hand,
        Array.Empty<MinionSnapshot>(),
        Array.Empty<LocationSnapshot>(),
        TargetDeckProfile.Cards.Select(card => new DeckEntrySnapshot(card.CardId, card.Count)),
        TargetDeckProfile.Cards.Select(card => new DeckEntrySnapshot(card.CardId, card.Count)),
        30,
        0,
        Array.Empty<ZoneCardSnapshot>(),
        Array.Empty<ZoneCardSnapshot>(),
        0);

    internal static GameObservation CreateObservation(FriendlyPlayerSnapshot friendly)
    {
        var opponent = new OpponentObservation(
            new HeroSnapshot(3, "HERO_08", 30, 30, 0, 0, false, false, 0, 1),
            new HeroPowerSnapshot(4, "CS2_034", 2, true, 0, 1),
            new[]
            {
                new ObservedCard(201, "HIDDEN_HAND_CARD", false),
                new ObservedCard(202, null, false)
            },
            Array.Empty<MinionSnapshot>(),
            Array.Empty<LocationSnapshot>(),
            28,
            0,
            Array.Empty<ObservedCard>(),
            new[]
            {
                new ObservedCard(203, "PUBLIC_CARD", true),
                new ObservedCard(204, "HIDDEN_REVEALED_CARD", false)
            },
            0,
            Array.Empty<string>());

        return new GameObservation(
            TargetRuntimeCompatibility.HearthstoneBuild,
            TargetRuntimeCompatibility.HdtVersion,
            TargetRuntimeCompatibility.CardDefsSha256,
            Guid.Parse("d4051f3d-0b0a-4470-b921-04bda2b57c79"),
            3,
            "MAIN_ACTION",
            "FRIENDLY",
            60000,
            true,
            friendly,
            opponent,
            new DerivedStateSnapshot(Array.Empty<PlatysaurBindingSnapshot>(), Array.Empty<int>(), 0, Array.Empty<string>()),
            new SensitiveGameMetadata("Player#1234", "account-secret", "server-secret", "C:\\Users\\secret"));
    }
}
