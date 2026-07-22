using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using DiscardAdvisor.Rules;

namespace DiscardAdvisor.Plugin;

public sealed class TrackedLocationState
{
    public TrackedLocationState(int entityId, int durability, int cooldown, bool available)
    {
        EntityId = entityId;
        Durability = durability;
        Cooldown = cooldown;
        Available = available;
    }

    public int EntityId { get; }
    public int Durability { get; }
    public int Cooldown { get; }
    public bool Available { get; }
}

public sealed class SpecialMechanicsState
{
    internal SpecialMechanicsState(
        int discardCount,
        IReadOnlyDictionary<int, int> platysaurBindings,
        IReadOnlyCollection<int> temporaryEntityIds,
        int shredsOfTimeInDeck,
        IReadOnlyDictionary<int, TrackedLocationState> locations)
    {
        DiscardCount = discardCount;
        PlatysaurBindings = platysaurBindings;
        TemporaryEntityIds = temporaryEntityIds;
        ShredsOfTimeInDeck = shredsOfTimeInDeck;
        Locations = locations;
    }

    public int DiscardCount { get; }
    public IReadOnlyDictionary<int, int> PlatysaurBindings { get; }
    public IReadOnlyCollection<int> TemporaryEntityIds { get; }
    public int ShredsOfTimeInDeck { get; }
    public IReadOnlyDictionary<int, TrackedLocationState> Locations { get; }
}

public sealed class SpecialMechanicsTracker
{
    private readonly object _gate = new();
    private readonly Queue<int> _platysaursAwaitingDraw = new();
    private readonly Dictionary<int, int> _platysaurBindings = new();
    private readonly HashSet<int> _temporaryEntityIds = new();
    private readonly Dictionary<int, TrackedLocationState> _locations = new();
    private int _soulariumDrawsRemaining;
    private int _cursedCatacombsGetsRemaining;
    private int _discardCount;
    private int _shredsOfTimeInDeck;

    public void Reset()
    {
        lock (_gate)
        {
            _platysaursAwaitingDraw.Clear();
            _platysaurBindings.Clear();
            _temporaryEntityIds.Clear();
            _locations.Clear();
            _soulariumDrawsRemaining = 0;
            _cursedCatacombsGetsRemaining = 0;
            _discardCount = 0;
            _shredsOfTimeInDeck = 0;
        }
    }

    public void RecordCardPlayed(string cardId, int entityId)
    {
        lock (_gate)
        {
            _temporaryEntityIds.Remove(entityId);
            switch (cardId)
            {
                case DiscardWarlockCardIds.Platysaur:
                    _platysaursAwaitingDraw.Enqueue(entityId);
                    break;
                case DiscardWarlockCardIds.Soularium:
                    _soulariumDrawsRemaining += 3;
                    break;
                case DiscardWarlockCardIds.CursedCatacombs:
                    _cursedCatacombsGetsRemaining++;
                    break;
                case DiscardWarlockCardIds.EntropicContinuity:
                    _shredsOfTimeInDeck += 2;
                    break;
            }
        }
    }

    public void RecordCardDrawn(string cardId, int entityId)
    {
        lock (_gate)
        {
            if (_platysaursAwaitingDraw.Count > 0)
                _platysaurBindings[_platysaursAwaitingDraw.Dequeue()] = entityId;
            if (_soulariumDrawsRemaining > 0)
            {
                _temporaryEntityIds.Add(entityId);
                _soulariumDrawsRemaining--;
            }
            if (cardId == DiscardWarlockCardIds.ShredOfTime)
                _shredsOfTimeInDeck = Math.Max(0, _shredsOfTimeInDeck - 1);
        }
    }

    public void RecordCardCreatedInHand(int entityId)
    {
        lock (_gate)
        {
            if (_cursedCatacombsGetsRemaining <= 0)
                return;
            _temporaryEntityIds.Add(entityId);
            _cursedCatacombsGetsRemaining--;
        }
    }

    public void RecordCardDiscarded(int entityId)
    {
        lock (_gate)
        {
            _discardCount++;
            _temporaryEntityIds.Remove(entityId);
        }
    }

    public void RecordCardLeftHand(int entityId)
    {
        lock (_gate)
            _temporaryEntityIds.Remove(entityId);
    }

    public void RecordLocation(int entityId, int durability, int cooldown, bool available)
    {
        lock (_gate)
            _locations[entityId] = new TrackedLocationState(entityId, durability, cooldown, available);
    }

    public SpecialMechanicsState Capture(
        IEnumerable<int> handEntityIds,
        IEnumerable<int> livingPlatysaurEntityIds,
        int observedDiscardCount,
        int observedShredsOfTime)
    {
        lock (_gate)
        {
            var hand = new HashSet<int>(handEntityIds);
            var livingPlatysaurs = new HashSet<int>(livingPlatysaurEntityIds);
            _temporaryEntityIds.IntersectWith(hand);
            foreach (var staleBinding in _platysaurBindings.Keys.Where(entityId => !livingPlatysaurs.Contains(entityId)).ToArray())
                _platysaurBindings.Remove(staleBinding);
            _discardCount = Math.Max(_discardCount, observedDiscardCount);
            if (observedShredsOfTime >= 0)
                _shredsOfTimeInDeck = Math.Max(_shredsOfTimeInDeck, observedShredsOfTime);

            var snapshot = new SpecialMechanicsState(
                _discardCount,
                new ReadOnlyDictionary<int, int>(new Dictionary<int, int>(_platysaurBindings)),
                new ReadOnlyCollection<int>(_temporaryEntityIds.OrderBy(entityId => entityId).ToArray()),
                _shredsOfTimeInDeck,
                new ReadOnlyDictionary<int, TrackedLocationState>(new Dictionary<int, TrackedLocationState>(_locations)));

            // A capture happens only after the event stream has been stable for the debounce window.
            // Any unresolved one-shot expectation belongs to an effect that produced no entity.
            _platysaursAwaitingDraw.Clear();
            _soulariumDrawsRemaining = 0;
            _cursedCatacombsGetsRemaining = 0;
            return snapshot;
        }
    }
}
