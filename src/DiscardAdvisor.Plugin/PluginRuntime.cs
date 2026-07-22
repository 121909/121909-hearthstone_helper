using System;
using DiscardAdvisor.Domain;
using DiscardAdvisor.Domain.Snapshots;

namespace DiscardAdvisor.Plugin;

public sealed class PluginRuntime : IPluginRuntime, IDisposable
{
    private readonly PluginLifetime _lifetime;
    private readonly IGameContextProvider? _gameContextProvider;
    private readonly IGameEventSource? _gameEventSource;
    private readonly ISnapshotObservationSource? _snapshotSource;
    private readonly DeckSupportGate _supportGate = new();
    private readonly GameSnapshotBuilder _snapshotBuilder = new();
    private readonly SnapshotCoordinator _snapshotCoordinator;
    private Guid _gameId;

    public PluginRuntime()
        : this(new PluginLifetime(), null, null, null, new SnapshotCoordinator())
    {
    }

    public PluginRuntime(IGameContextProvider gameContextProvider)
        : this(new PluginLifetime(), gameContextProvider, null, null, new SnapshotCoordinator())
    {
    }

    public PluginRuntime(
        IGameContextProvider gameContextProvider,
        IGameEventSource gameEventSource,
        ISnapshotObservationSource snapshotSource)
        : this(new PluginLifetime(), gameContextProvider, gameEventSource, snapshotSource, new SnapshotCoordinator())
    {
    }

    internal PluginRuntime(
        PluginLifetime lifetime,
        IGameContextProvider? gameContextProvider = null,
        IGameEventSource? gameEventSource = null,
        ISnapshotObservationSource? snapshotSource = null,
        SnapshotCoordinator? snapshotCoordinator = null)
    {
        _lifetime = lifetime;
        _gameContextProvider = gameContextProvider;
        _gameEventSource = gameEventSource;
        _snapshotSource = snapshotSource;
        _snapshotCoordinator = snapshotCoordinator ?? new SnapshotCoordinator();
    }

    public PluginRunState State => _lifetime.State;

    public GateDecision? CurrentGateDecision { get; private set; }

    public event Action<SnapshotWorkItem>? SnapshotReady;

    public void Start()
    {
        if (_lifetime.State == PluginRunState.Running)
            return;
        _lifetime.Start();
        _gameId = Guid.NewGuid();
        _gameEventSource?.Start(HandleGameStarted, HandleGameEnded, HandleStateChanged);
        RefreshEligibility();
        if (CurrentGateDecision?.IsEnabled == true)
            _snapshotCoordinator.MarkDirty();
    }

    public void Stop()
    {
        _lifetime.Stop();
        _gameEventSource?.Stop();
        _snapshotCoordinator.Reset();
        CurrentGateDecision = null;
    }

    public void Update()
    {
        // HDT calls this on its update thread. Future work is limited to cheap dirty-state checks here.
        if (!_lifetime.TryGetSession(out _) || _snapshotSource is null || CurrentGateDecision?.IsEnabled != true)
            return;

        if (_snapshotCoordinator.TryCreateWork(CaptureSnapshot, out var workItem) && workItem is not null)
            SnapshotReady?.Invoke(workItem);
    }

    public void RefreshEligibility()
    {
        if (_gameContextProvider is null || !_lifetime.TryGetSession(out _))
            return;

        var context = _gameContextProvider.CaptureGateContext();
        CurrentGateDecision = _supportGate.Evaluate(context.GameMode, context.DeckCardIds, context.Compatibility);
    }

    public void Dispose()
    {
        Stop();
        _snapshotCoordinator.Dispose();
    }

    public bool CanAcceptResult(string stateId) => _snapshotCoordinator.CanAcceptResult(stateId);

    private GameSnapshot? CaptureSnapshot()
    {
        if (_snapshotSource is null || !_snapshotSource.TryCapture(_gameId, true, out var observation) || observation is null)
            return null;
        return _snapshotBuilder.Build(observation);
    }

    private void HandleGameStarted()
    {
        _gameId = Guid.NewGuid();
        HandleStateChanged();
    }

    private void HandleGameEnded()
    {
        _snapshotCoordinator.Reset();
        CurrentGateDecision = null;
    }

    private void HandleStateChanged()
    {
        RefreshEligibility();
        _snapshotCoordinator.MarkDirty();
    }
}
