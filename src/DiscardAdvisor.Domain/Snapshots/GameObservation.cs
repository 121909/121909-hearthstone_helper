using System;
using System.Collections.Generic;

namespace DiscardAdvisor.Domain.Snapshots;

public sealed class ObservedCard
{
    public ObservedCard(int entityId, string? cardId, bool isPublic, int? createdByEntityId = null)
    {
        EntityId = entityId;
        CardId = cardId;
        IsPublic = isPublic;
        CreatedByEntityId = createdByEntityId;
    }

    public int EntityId { get; }
    public string? CardId { get; }
    public bool IsPublic { get; }
    public int? CreatedByEntityId { get; }
}

public sealed class SensitiveGameMetadata
{
    public SensitiveGameMetadata(string? battleTag, string? accountId, string? serverInfo, string? localPath)
    {
        BattleTag = battleTag;
        AccountId = accountId;
        ServerInfo = serverInfo;
        LocalPath = localPath;
    }

    public string? BattleTag { get; }
    public string? AccountId { get; }
    public string? ServerInfo { get; }
    public string? LocalPath { get; }
}

public sealed class OpponentObservation
{
    public OpponentObservation(
        HeroSnapshot hero,
        HeroPowerSnapshot heroPower,
        IEnumerable<ObservedCard> hand,
        IEnumerable<MinionSnapshot> board,
        IEnumerable<LocationSnapshot> locations,
        int deckCount,
        int fatigue,
        IEnumerable<ObservedCard> graveyard,
        IEnumerable<ObservedCard> revealedCards,
        int secretCount,
        IEnumerable<string> secretCandidates,
        WeaponSnapshot? weapon = null,
        string? questCardId = null)
    {
        Hero = hero ?? throw new ArgumentNullException(nameof(hero));
        Weapon = weapon;
        HeroPower = heroPower ?? throw new ArgumentNullException(nameof(heroPower));
        Hand = SnapshotCollections.Freeze(hand);
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
    public IReadOnlyList<ObservedCard> Hand { get; }
    public IReadOnlyList<MinionSnapshot> Board { get; }
    public IReadOnlyList<LocationSnapshot> Locations { get; }
    public int DeckCount { get; }
    public int Fatigue { get; }
    public IReadOnlyList<ObservedCard> Graveyard { get; }
    public IReadOnlyList<ObservedCard> RevealedCards { get; }
    public int SecretCount { get; }
    public IReadOnlyList<string> SecretCandidates { get; }
    public string? QuestCardId { get; }
}

public sealed class GameObservation
{
    public GameObservation(
        int hearthstoneBuild,
        string hdtVersion,
        string cardDefsHash,
        Guid gameId,
        string stateId,
        int turnNumber,
        string step,
        string activePlayer,
        int remainingTurnTimeMs,
        bool isStable,
        FriendlyPlayerSnapshot friendly,
        OpponentObservation opponent,
        DerivedStateSnapshot derived,
        SensitiveGameMetadata sensitiveMetadata,
        IEnumerable<SnapshotAction>? actionsThisTurn = null,
        ChoiceSnapshot? currentChoice = null)
    {
        HearthstoneBuild = hearthstoneBuild;
        HdtVersion = hdtVersion ?? throw new ArgumentNullException(nameof(hdtVersion));
        CardDefsHash = cardDefsHash ?? throw new ArgumentNullException(nameof(cardDefsHash));
        GameId = gameId;
        StateId = stateId ?? throw new ArgumentNullException(nameof(stateId));
        TurnNumber = turnNumber;
        Step = step ?? throw new ArgumentNullException(nameof(step));
        ActivePlayer = activePlayer ?? throw new ArgumentNullException(nameof(activePlayer));
        RemainingTurnTimeMs = remainingTurnTimeMs;
        IsStable = isStable;
        Friendly = friendly ?? throw new ArgumentNullException(nameof(friendly));
        Opponent = opponent ?? throw new ArgumentNullException(nameof(opponent));
        Derived = derived ?? throw new ArgumentNullException(nameof(derived));
        SensitiveMetadata = sensitiveMetadata ?? throw new ArgumentNullException(nameof(sensitiveMetadata));
        ActionsThisTurn = SnapshotCollections.Freeze(actionsThisTurn ?? Array.Empty<SnapshotAction>());
        CurrentChoice = currentChoice;
    }

    public int HearthstoneBuild { get; }
    public string HdtVersion { get; }
    public string CardDefsHash { get; }
    public Guid GameId { get; }
    public string StateId { get; }
    public int TurnNumber { get; }
    public string Step { get; }
    public string ActivePlayer { get; }
    public int RemainingTurnTimeMs { get; }
    public bool IsStable { get; }
    public FriendlyPlayerSnapshot Friendly { get; }
    public OpponentObservation Opponent { get; }
    public DerivedStateSnapshot Derived { get; }
    public SensitiveGameMetadata SensitiveMetadata { get; }
    public IReadOnlyList<SnapshotAction> ActionsThisTurn { get; }
    public ChoiceSnapshot? CurrentChoice { get; }
}

