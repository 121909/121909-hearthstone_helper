using System;
using Hearthstone_Deck_Tracker.API;
using Hearthstone_Deck_Tracker.Utility.Assets;

namespace DiscardAdvisor.Plugin;

internal sealed class HdtGameEventSource : IGameEventSource
{
    private bool _subscribed;
    private bool _running;
    private Action? _gameStarted;
    private Action? _gameEnded;
    private Action? _stateChanged;

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
        GameEvents.OnPlayerDraw.Add(_ => NotifyStateChanged());
        GameEvents.OnPlayerPlay.Add(_ => NotifyStateChanged());
        GameEvents.OnPlayerHandDiscard.Add(_ => NotifyStateChanged());
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
        if (_running)
            _gameStarted?.Invoke();
    }

    private void NotifyGameEnded()
    {
        if (_running)
            _gameEnded?.Invoke();
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
}
