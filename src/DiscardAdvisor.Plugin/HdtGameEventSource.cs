using System;
using System.Collections.Generic;
using System.Linq;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using Hearthstone_Deck_Tracker.Utility.Assets;
using HdtApiCore = Hearthstone_Deck_Tracker.API.Core;

namespace DiscardAdvisor.Plugin;

internal sealed class HdtGameEventSource : IGameEventSource
{
    private readonly SpecialMechanicsTracker _mechanics;
    private readonly HashSet<int> _seenDrawEntities = new();
    private readonly HashSet<int> _seenCreatedEntities = new();
    private readonly HashSet<int> _seenPlayedEntities = new();
    private readonly HashSet<int> _seenDiscardedEntities = new();
    private volatile HdtGameEventRegistration? _activeRegistration;
    private bool _cardDefinitionsSubscribed;
    private volatile bool _running;
    private Action? _gameStarted;
    private Action? _gameEnded;
    private Action? _stateChanged;

    public HdtGameEventSource(SpecialMechanicsTracker mechanics)
    {
        _mechanics = mechanics;
    }

    public void Start(Action gameStarted, Action gameEnded, Action stateChanged)
    {
        _gameStarted = gameStarted ?? throw new ArgumentNullException(nameof(gameStarted));
        _gameEnded = gameEnded ?? throw new ArgumentNullException(nameof(gameEnded));
        _stateChanged = stateChanged ?? throw new ArgumentNullException(nameof(stateChanged));
        _running = true;
        var registration = new HdtGameEventRegistration(this);
        _activeRegistration = registration;
        DiscardAdvisorPlugin.RegisterGameEvents(registration);
        if (!_cardDefinitionsSubscribed)
        {
            CardDefsManager.CardsChanged += NotifyCardDefinitionsChanged;
            _cardDefinitionsSubscribed = true;
        }
    }

    public void Stop()
    {
        _running = false;
        _activeRegistration = null;
        _gameStarted = null;
        _gameEnded = null;
        _stateChanged = null;
        if (_cardDefinitionsSubscribed)
        {
            CardDefsManager.CardsChanged -= NotifyCardDefinitionsChanged;
            _cardDefinitionsSubscribed = false;
        }
    }

    internal void NotifyGameStarted()
    {
        if (!_running)
            return;
        _seenDrawEntities.Clear();
        _seenCreatedEntities.Clear();
        _seenPlayedEntities.Clear();
        _seenDiscardedEntities.Clear();
        _mechanics.Reset();
        _gameStarted?.Invoke();
    }

    internal void NotifyGameEnded()
    {
        if (!_running)
            return;
        _gameEnded?.Invoke();
        _mechanics.Reset();
    }

    internal void NotifyStateChanged()
    {
        if (_running)
            _stateChanged?.Invoke();
    }

    private void NotifyCardDefinitionsChanged()
    {
        HdtGameContextProvider.InvalidateCompatibility();
        NotifyStateChanged();
    }

    internal void NotifyPlayerPlay(Card card)
    {
        if (!_running)
            return;
        var entity = HdtApiCore.Game.Player.CardsPlayedThisTurn
            .LastOrDefault(candidate => candidate.CardId == card.Id && _seenPlayedEntities.Add(candidate.Id));
        if (entity is not null)
            _mechanics.RecordCardPlayed(card.Id, entity.Id);
        NotifyStateChanged();
    }

    internal void NotifyPlayerDraw(Card card)
    {
        if (!_running)
            return;
        var entity = FindLatestUnseenEntity(card.Id, _seenDrawEntities);
        if (entity is not null)
            _mechanics.RecordCardDrawn(card.Id, entity.Id);
        NotifyStateChanged();
    }

    internal void NotifyPlayerGet(Card card)
    {
        if (!_running)
            return;
        var entity = HdtApiCore.Game.Player.Hand
            .Where(candidate => candidate.CardId == card.Id && !_seenCreatedEntities.Contains(candidate.Id))
            .OrderByDescending(candidate => candidate.Id)
            .FirstOrDefault();
        if (entity is not null)
        {
            _seenCreatedEntities.Add(entity.Id);
            _mechanics.RecordCardCreatedInHand(entity.Id);
        }
        NotifyStateChanged();
    }

    internal void NotifyPlayerDiscard(Card card)
    {
        if (!_running)
            return;
        var entity = HdtApiCore.Game.Player.EntitiesDiscardedFromHand
            .LastOrDefault(candidate => candidate.CardId == card.Id && _seenDiscardedEntities.Add(candidate.Id));
        if (entity is not null)
            _mechanics.RecordCardDiscarded(entity.Id);
        NotifyStateChanged();
    }

    internal bool IsRegistrationActive(HdtGameEventRegistration registration) =>
        _running && ReferenceEquals(_activeRegistration, registration);

    private static Entity? FindLatestUnseenEntity(string cardId, ISet<int> seen)
    {
        var entity = HdtApiCore.Game.Player.PlayerEntities
            .Where(candidate => candidate.CardId == cardId && !seen.Contains(candidate.Id))
            .OrderByDescending(candidate => candidate.Info.Turn)
            .ThenByDescending(candidate => candidate.Id)
            .FirstOrDefault();
        if (entity is not null)
            seen.Add(entity.Id);
        return entity;
    }
}

internal sealed class HdtGameEventRegistration
{
    private readonly WeakReference<HdtGameEventSource> _source;

    public HdtGameEventRegistration(HdtGameEventSource source)
    {
        _source = new WeakReference<HdtGameEventSource>(source);
    }

    public void NotifyGameStarted() => Invoke(source => source.NotifyGameStarted());

    public void NotifyGameEnded() => Invoke(source => source.NotifyGameEnded());

    public void NotifyStateChanged() => Invoke(source => source.NotifyStateChanged());

    public void NotifyPlayerPlay(Card card) => Invoke(source => source.NotifyPlayerPlay(card));

    public void NotifyPlayerDraw(Card card) => Invoke(source => source.NotifyPlayerDraw(card));

    public void NotifyPlayerGet(Card card) => Invoke(source => source.NotifyPlayerGet(card));

    public void NotifyPlayerDiscard(Card card) => Invoke(source => source.NotifyPlayerDiscard(card));

    private void Invoke(Action<HdtGameEventSource> action)
    {
        if (_source.TryGetTarget(out var source) && source.IsRegistrationActive(this))
            action(source);
    }
}
