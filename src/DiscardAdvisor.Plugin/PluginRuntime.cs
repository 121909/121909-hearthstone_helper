using System;
using System.Threading.Tasks;
using DiscardAdvisor.Domain;
using DiscardAdvisor.Domain.Snapshots;

namespace DiscardAdvisor.Plugin;

public sealed class PluginRuntime : IPluginRuntime, IOverlayStateSource, IDisposable
{
    private readonly PluginLifetime _lifetime;
    private readonly IGameContextProvider? _gameContextProvider;
    private readonly IGameEventSource? _gameEventSource;
    private readonly ISnapshotObservationSource? _snapshotSource;
    private readonly DeckSupportGate _supportGate = new();
    private readonly GameSnapshotBuilder _snapshotBuilder = new();
    private readonly SnapshotCoordinator _snapshotCoordinator;
    private readonly IPluginDiagnostics _diagnostics;
    private readonly ILocalAdvisorService? _advisorService;
    private readonly object _advisorStateGate = new();
    private PluginAdvisorUpdate _currentAdvisorUpdate = PluginAdvisorUpdate.StateOnly(PluginAdvisorStatus.Offline);
    private GateStatus? _lastRecordedGateStatus;
    private Guid _gameId;

    public PluginRuntime()
        : this(new PluginLifetime(), null, null, null, new SnapshotCoordinator(), NullPluginDiagnostics.Instance, null)
    {
    }

    public PluginRuntime(IGameContextProvider gameContextProvider)
        : this(new PluginLifetime(), gameContextProvider, null, null, new SnapshotCoordinator(), NullPluginDiagnostics.Instance, null)
    {
    }

    public PluginRuntime(
        IGameContextProvider gameContextProvider,
        IGameEventSource gameEventSource,
        ISnapshotObservationSource snapshotSource,
        IPluginDiagnostics? diagnostics = null,
        ILocalAdvisorService? advisorService = null)
        : this(
            new PluginLifetime(),
            gameContextProvider,
            gameEventSource,
            snapshotSource,
            new SnapshotCoordinator(),
            diagnostics ?? NullPluginDiagnostics.Instance,
            advisorService)
    {
    }

    internal PluginRuntime(
        PluginLifetime lifetime,
        IGameContextProvider? gameContextProvider = null,
        IGameEventSource? gameEventSource = null,
        ISnapshotObservationSource? snapshotSource = null,
        SnapshotCoordinator? snapshotCoordinator = null,
        IPluginDiagnostics? diagnostics = null,
        ILocalAdvisorService? advisorService = null)
    {
        _lifetime = lifetime;
        _gameContextProvider = gameContextProvider;
        _gameEventSource = gameEventSource;
        _snapshotSource = snapshotSource;
        _snapshotCoordinator = snapshotCoordinator ?? new SnapshotCoordinator();
        _diagnostics = diagnostics ?? NullPluginDiagnostics.Instance;
        _advisorService = advisorService;
    }

    public PluginRunState State => _lifetime.State;

    public GateDecision? CurrentGateDecision { get; private set; }

    public PluginAdvisorUpdate CurrentAdvisorUpdate
    {
        get
        {
            lock (_advisorStateGate)
                return _currentAdvisorUpdate;
        }
    }

    public event Action<SnapshotWorkItem>? SnapshotReady;

    public event Action<PluginAdvisorUpdate>? AdvisorUpdated;

    public void Start()
    {
        if (_lifetime.State == PluginRunState.Running)
            return;
        _lifetime.Start();
        _gameId = Guid.NewGuid();
        _gameEventSource?.Start(HandleGameStarted, HandleGameEnded, HandleStateChanged);
        RefreshEligibility();
        if (CurrentGateDecision?.IsEnabled == true)
        {
            PublishAdvisorUpdate(PluginAdvisorUpdate.StateOnly(PluginAdvisorStatus.Analyzing));
            _snapshotCoordinator.MarkDirty();
        }
    }

    public void Stop()
    {
        _lifetime.Stop();
        _gameEventSource?.Stop();
        _snapshotCoordinator.Reset();
        CurrentGateDecision = null;
        _lastRecordedGateStatus = null;
        PublishAdvisorUpdate(PluginAdvisorUpdate.StateOnly(PluginAdvisorStatus.Offline));
    }

    public void Update()
    {
        // HDT calls this on its update thread. Future work is limited to cheap dirty-state checks here.
        if (!_lifetime.TryGetSession(out _) || _snapshotSource is null || CurrentGateDecision?.IsEnabled != true)
            return;

        try
        {
            if (_snapshotCoordinator.TryCreateWork(CaptureSnapshot, out var workItem) && workItem is not null)
            {
                _diagnostics.RecordSnapshot(workItem.Snapshot);
                PublishAdvisorUpdate(PluginAdvisorUpdate.StateOnly(PluginAdvisorStatus.Analyzing, workItem.StateId));
                SnapshotReady?.Invoke(workItem);
                if (_advisorService is not null)
                    _ = RunAdvisorAsync(workItem);
            }
        }
        catch (Exception exception)
        {
            _diagnostics.RecordError("snapshot_capture_failed", exception);
            _snapshotCoordinator.MarkDirty();
        }
    }

    public void RefreshEligibility()
    {
        if (_gameContextProvider is null || !_lifetime.TryGetSession(out _))
            return;

        var context = _gameContextProvider.CaptureGateContext();
        CurrentGateDecision = _supportGate.Evaluate(context.GameMode, context.DeckCardIds, context.Compatibility);
        if (_lastRecordedGateStatus != CurrentGateDecision.Status)
        {
            _diagnostics.RecordGateDecision(CurrentGateDecision);
            _lastRecordedGateStatus = CurrentGateDecision.Status;
        }
        if (!CurrentGateDecision.IsEnabled)
        {
            var status = CurrentGateDecision.Status is GateStatus.UnsupportedPatch or
                GateStatus.UnsupportedHdtVersion or
                GateStatus.UnsupportedCardDefinitions or
                GateStatus.UnsupportedHearthDb
                ? PluginAdvisorStatus.UnsupportedPatch
                : PluginAdvisorStatus.Inactive;
            PublishAdvisorUpdate(PluginAdvisorUpdate.StateOnly(status));
        }
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
        _lastRecordedGateStatus = null;
        PublishAdvisorUpdate(PluginAdvisorUpdate.StateOnly(PluginAdvisorStatus.Offline));
    }

    private void HandleStateChanged()
    {
        PublishAdvisorUpdate(PluginAdvisorUpdate.StateOnly(PluginAdvisorStatus.Stale));
        RefreshEligibility();
        _snapshotCoordinator.MarkDirty();
    }

    private async Task RunAdvisorAsync(SnapshotWorkItem workItem)
    {
        try
        {
            var update = await _advisorService!.AnalyzeAsync(workItem.Snapshot, workItem.CancellationToken)
                .ConfigureAwait(false);
            if (workItem.CancellationToken.IsCancellationRequested || !_snapshotCoordinator.CanAcceptResult(workItem.StateId))
                return;
            PublishAdvisorUpdate(update);
        }
        catch (OperationCanceledException) when (workItem.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _diagnostics.RecordError("local_advisor_failed", exception);
            if (_snapshotCoordinator.CanAcceptResult(workItem.StateId))
                PublishAdvisorUpdate(PluginAdvisorUpdate.StateOnly(PluginAdvisorStatus.Offline, workItem.StateId));
        }
    }

    private void PublishAdvisorUpdate(PluginAdvisorUpdate update)
    {
        lock (_advisorStateGate)
            _currentAdvisorUpdate = update;
        AdvisorUpdated?.Invoke(update);
    }
}
