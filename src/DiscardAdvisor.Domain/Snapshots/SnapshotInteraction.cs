using System;
using System.Collections.Generic;

namespace DiscardAdvisor.Domain.Snapshots;

public sealed class EntityReferenceSnapshot
{
    public EntityReferenceSnapshot(int entityId, string? cardId = null)
    {
        EntityId = entityId;
        CardId = cardId;
    }

    public int EntityId { get; }
    public string? CardId { get; }
}

public sealed class ChoiceSnapshot
{
    public ChoiceSnapshot(int choiceId, string choiceType, IEnumerable<EntityReferenceSnapshot> candidates, int? sourceEntityId = null)
    {
        ChoiceId = choiceId;
        ChoiceType = choiceType ?? throw new ArgumentNullException(nameof(choiceType));
        Candidates = SnapshotCollections.Freeze(candidates);
        SourceEntityId = sourceEntityId;
    }

    public int ChoiceId { get; }
    public string ChoiceType { get; }
    public int? SourceEntityId { get; }
    public IReadOnlyList<EntityReferenceSnapshot> Candidates { get; }
}

public sealed class PlatysaurBindingSnapshot
{
    public PlatysaurBindingSnapshot(int platysaurEntityId, int drawnEntityId)
    {
        PlatysaurEntityId = platysaurEntityId;
        DrawnEntityId = drawnEntityId;
    }

    public int PlatysaurEntityId { get; }
    public int DrawnEntityId { get; }
}

public sealed class DerivedStateSnapshot
{
    public DerivedStateSnapshot(
        IEnumerable<PlatysaurBindingSnapshot> platysaurBindings,
        IEnumerable<int> temporaryEntityIds,
        int shredsOfTimeInDeck,
        IEnumerable<string> unsupportedInteractions)
    {
        PlatysaurBindings = SnapshotCollections.Freeze(platysaurBindings);
        TemporaryEntityIds = SnapshotCollections.Freeze(temporaryEntityIds);
        ShredsOfTimeInDeck = shredsOfTimeInDeck;
        UnsupportedInteractions = SnapshotCollections.Freeze(unsupportedInteractions);
    }

    public IReadOnlyList<PlatysaurBindingSnapshot> PlatysaurBindings { get; }
    public IReadOnlyList<int> TemporaryEntityIds { get; }
    public int ShredsOfTimeInDeck { get; }
    public IReadOnlyList<string> UnsupportedInteractions { get; }
}

public abstract class SnapshotAction
{
    protected SnapshotAction(string actionType)
    {
        ActionType = actionType ?? throw new ArgumentNullException(nameof(actionType));
    }

    public string ActionType { get; }
}

