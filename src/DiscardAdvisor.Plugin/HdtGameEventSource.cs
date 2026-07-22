using System;
using System.Collections.Generic;
using System.Linq;
using Hearthstone_Deck_Tracker.API;
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
    private bool _subscribed;
    private bool _running;
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
        if (_subscribed)
            return;

        GameEvents.OnGameStart.Add(NotifyGameStarted);
        GameEvents.OnGameEnd.Add(NotifyGameEnded);
        GameEvents.OnTurnStart.Add(_ => NotifyStateChanged());
        GameEvents.OnPlayerDraw.Add(NotifyPlayerDraw);
        GameEvents.OnPlayerGet.Add(NotifyPlayerGet);
        GameEvents.OnPlayerPlay.Add(NotifyPlayerPlay);
        GameEvents.OnPlayerHandDiscard.Add(NotifyPlayerDiscard);
        GameEvents.OnOpponentPlay.Add(_ => NotifyStateChanged());
        GameEvents.OnOpponentHandDiscard.Add(_ => NotifyStateChanged());
        GameEvents.OnPlayerMinionAttack.Add(_ => NotifyStateChanged());
        GameEvents.OnOpponentMinionAttack.Add(_ => NotifyStateChanged());
        GameEvents.OnEntityWillTakeDamage.Add(_ => NotifyStateChanged());
        GameEvents.OnModeChanged.Add(_ => NotifyStateChanged());
        CardDefsManager.CardsChanged += NotifyCardDefinitionsChanged;
        _subscribed = true;
    }

    public void Stop()
    {
        _running = false;
        _gameStarted = null;
        _gameEnded = null;
        _stateChanged = null;
    }

    private void NotifyGameStarted()
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

    private void NotifyGameEnded()
    {
        if (!_running)
            return;
        _gameEnded?.Invoke();
        _mechanics.Reset();
    }

    private void NotifyStateChanged()
    {
        if (_running)
            _stateChanged?.Invoke();
    }

    private void NotifyCardDefinitionsChanged()
    {
        HdtGameContextProvider.InvalidateCompatibility();
        NotifyStateChanged();
    }

    private void NotifyPlayerPlay(Card card)
    {
        if (!_running)
            return;
        var entity = HdtApiCore.Game.Player.CardsPlayedThisTurn
            .LastOrDefault(candidate => candidate.CardId == card.Id && _seenPlayedEntities.Add(candidate.Id));
        if (entity is not null)
            _mechanics.RecordCardPlayed(card.Id, entity.Id);
        NotifyStateChanged();
    }

    private void NotifyPlayerDraw(Card card)
    {
        if (!_running)
            return;
        var entity = FindLatestUnseenEntity(card.Id, _seenDrawEntities);
        if (entity is not null)
            _mechanics.RecordCardDrawn(card.Id, entity.Id);
        NotifyStateChanged();
    }

    private void NotifyPlayerGet(Card card)
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

    private void NotifyPlayerDiscard(Card card)
    {
        if (!_running)
            return;
        var entity = HdtApiCore.Game.Player.EntitiesDiscardedFromHand
            .LastOrDefault(candidate => candidate.CardId == card.Id && _seenDiscardedEntities.Add(candidate.Id));
        if (entity is not null)
            _mechanics.RecordCardDiscarded(entity.Id);
        NotifyStateChanged();
    }

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
