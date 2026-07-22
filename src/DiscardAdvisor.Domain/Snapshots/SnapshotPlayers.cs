using System;
using System.Collections.Generic;

namespace DiscardAdvisor.Domain.Snapshots;

public sealed class FriendlyPlayerSnapshot
{
    public FriendlyPlayerSnapshot(
        HeroSnapshot hero,
        HeroPowerSnapshot heroPower,
        ManaSnapshot mana,
        IEnumerable<HandCardSnapshot> hand,
        IEnumerable<MinionSnapshot> board,
        IEnumerable<LocationSnapshot> locations,
        IEnumerable<DeckEntrySnapshot> originalDeck,
        IEnumerable<DeckEntrySnapshot> knownRemainingDeck,
        int deckCount,
        int fatigue,
        IEnumerable<ZoneCardSnapshot> graveyard,
        IEnumerable<ZoneCardSnapshot> discarded,
        int discardCount,
        WeaponSnapshot? weapon = null)
    {
        Hero = hero ?? throw new ArgumentNullException(nameof(hero));
        Weapon = weapon;
        HeroPower = heroPower ?? throw new ArgumentNullException(nameof(heroPower));
        Mana = mana ?? throw new ArgumentNullException(nameof(mana));
        Hand = SnapshotCollections.Freeze(hand);
        Board = SnapshotCollections.Freeze(board);
        Locations = SnapshotCollections.Freeze(locations);
        OriginalDeck = SnapshotCollections.Freeze(originalDeck);
        KnownRemainingDeck = SnapshotCollections.Freeze(knownRemainingDeck);
        DeckCount = deckCount;
        Fatigue = fatigue;
        Graveyard = SnapshotCollections.Freeze(graveyard);
        Discarded = SnapshotCollections.Freeze(discarded);
        DiscardCount = discardCount;
    }

    public HeroSnapshot Hero { get; }
    public WeaponSnapshot? Weapon { get; }
    public HeroPowerSnapshot HeroPower { get; }
    public ManaSnapshot Mana { get; }
    public IReadOnlyList<HandCardSnapshot> Hand { get; }
    public IReadOnlyList<MinionSnapshot> Board { get; }
    public IReadOnlyList<LocationSnapshot> Locations { get; }
    public IReadOnlyList<DeckEntrySnapshot> OriginalDeck { get; }
    public IReadOnlyList<DeckEntrySnapshot> KnownRemainingDeck { get; }
    public int DeckCount { get; }
    public int Fatigue { get; }
    public IReadOnlyList<ZoneCardSnapshot> Graveyard { get; }
    public IReadOnlyList<ZoneCardSnapshot> Discarded { get; }
    public int DiscardCount { get; }
}

public sealed class OpponentPlayerSnapshot
{
    public OpponentPlayerSnapshot(
        HeroSnapshot hero,
        HeroPowerSnapshot heroPower,
        int handCount,
        IEnumerable<MinionSnapshot> board,
        IEnumerable<LocationSnapshot> locations,
        int deckCount,
        int fatigue,
        IEnumerable<ZoneCardSnapshot> graveyard,
        IEnumerable<DeckEntrySnapshot> revealedCards,
        int secretCount,
        IEnumerable<string> secretCandidates,
        WeaponSnapshot? weapon = null,
        string? questCardId = null)
    {
        Hero = hero ?? throw new ArgumentNullException(nameof(hero));
        Weapon = weapon;
        HeroPower = heroPower ?? throw new ArgumentNullException(nameof(heroPower));
        HandCount = handCount;
        Board = SnapshotCollections.Freeze(board);
        Locations = SnapshotCollections.Freeze(locations);
        DeckCount = deckCount;
        Fatigue = fatigue;
        Graveyard = SnapshotCollections.Freeze(graveyard);
        RevealedCards = SnapshotCollections.Freeze(revealedCards);
        SecretCount = secretCount;
        SecretCandidates = SnapshotCollections.Freeze(secretCandidates);
        QuestCardId = questCardId;
    }

    public HeroSnapshot Hero { get; }
    public WeaponSnapshot? Weapon { get; }
    public HeroPowerSnapshot HeroPower { get; }
    public int HandCount { get; }
    public IReadOnlyList<MinionSnapshot> Board { get; }
    public IReadOnlyList<LocationSnapshot> Locations { get; }
    public int DeckCount { get; }
    public int Fatigue { get; }
    public IReadOnlyList<ZoneCardSnapshot> Graveyard { get; }
    public IReadOnlyList<DeckEntrySnapshot> RevealedCards { get; }
    public int SecretCount { get; }
    public IReadOnlyList<string> SecretCandidates { get; }
    public string? QuestCardId { get; }
}

