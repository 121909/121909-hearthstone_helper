using System;
using System.Collections.Generic;

namespace DiscardAdvisor.Domain.Snapshots;

public sealed class GameSnapshot
{
    public GameSnapshot(
        string ruleSetVersion,
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
        OpponentPlayerSnapshot opponent,
        IEnumerable<SnapshotAction> actionsThisTurn,
        DerivedStateSnapshot derived,
        ChoiceSnapshot? currentChoice = null)
    {
        RuleSetVersion = ruleSetVersion ?? throw new ArgumentNullException(nameof(ruleSetVersion));
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
        ActionsThisTurn = SnapshotCollections.Freeze(actionsThisTurn);
        CurrentChoice = currentChoice;
        Derived = derived ?? throw new ArgumentNullException(nameof(derived));
    }

    public string ProtocolVersion => "1.0.0";
    public string RuleSetVersion { get; }
    public int HearthstoneBuild { get; }
    public string HdtVersion { get; }
    public string CardDefsHash { get; }
    public Guid GameId { get; }
    public string GameMode => TargetDeckProfile.GameMode;
    public string StateId { get; }
    public int TurnNumber { get; }
    public string Step { get; }
    public string ActivePlayer { get; }
    public int RemainingTurnTimeMs { get; }
    public bool IsStable { get; }
    public FriendlyPlayerSnapshot Friendly { get; }
    public OpponentPlayerSnapshot Opponent { get; }
    public IReadOnlyList<SnapshotAction> ActionsThisTurn { get; }
    public ChoiceSnapshot? CurrentChoice { get; }
    public DerivedStateSnapshot Derived { get; }

    internal GameSnapshot WithStateId(string stateId) => new(
        RuleSetVersion,
        HearthstoneBuild,
        HdtVersion,
        CardDefsHash,
        GameId,
        stateId,
        TurnNumber,
        Step,
        ActivePlayer,
        RemainingTurnTimeMs,
        IsStable,
        Friendly,
        Opponent,
        ActionsThisTurn,
        Derived,
        CurrentChoice);
}
