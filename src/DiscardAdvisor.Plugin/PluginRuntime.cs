using System;
using System.Diagnostics;
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
    private readonly IAutomationAdviceSink _automationAdviceSink;
    private readonly object _advisorStateGate = new();
    private PluginAdvisorUpdate _currentAdvisorUpdate = PluginAdvisorUpdate.StateOnly(PluginAdvisorStatus.Offline);
    private PluginAdvisorUpdate? _lastCompletedAdvisorUpdate;
    private GateStatus? _lastRecordedGateStatus;
    private SnapshotCaptureFailure? _lastRecordedSnapshotCaptureFailure;
    private Guid _gameId;
    private bool _gameSessionActive;

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
        ILocalAdvisorService? advisorService = null,
        IAutomationAdviceSink? automationAdviceSink = null)
        : this(
            new PluginLifetime(),
            gameContextProvider,
            gameEventSource,
            snapshotSource,
            new SnapshotCoordinator(),
            diagnostics ?? NullPluginDiagnostics.Instance,
            advisorService,
            automationAdviceSink)
    {
    }

    internal PluginRuntime(
        PluginLifetime lifetime,
        IGameContextProvider? gameContextProvider = null,
        IGameEventSource? gameEventSource = null,
        ISnapshotObservationSource? snapshotSource = null,
        SnapshotCoordinator? snapshotCoordinator = null,
        IPluginDiagnostics? diagnostics = null,
        ILocalAdvisorService? advisorService = null,
        IAutomationAdviceSink? automationAdviceSink = null)
    {
        _lifetime = lifetime;
        _gameContextProvider = gameContextProvider;
        _gameEventSource = gameEventSource;
        _snapshotSource = snapshotSource;
        _snapshotCoordinator = snapshotCoordinator ?? new SnapshotCoordinator();
        _diagnostics = diagnostics ?? NullPluginDiagnostics.Instance;
        _advisorService = advisorService;
        _automationAdviceSink = automationAdviceSink ?? NullAutomationAdviceSink.Instance;
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
        if (_gameSessionActive)
        {
            _diagnostics.RecordGameEnded(_gameId, completed: false);
            _gameSessionActive = false;
        }
        _lifetime.Stop();
        _gameEventSource?.Stop();
        _snapshotCoordinator.Reset();
        lock (_advisorStateGate)
            _lastCompletedAdvisorUpdate = null;
        CurrentGateDecision = null;
        _lastRecordedGateStatus = null;
        _lastRecordedSnapshotCaptureFailure = null;
        PublishAdvisorUpdate(PluginAdvisorUpdate.StateOnly(PluginAdvisorStatus.Offline));
    }

    public void Update()
    {
        // HDT calls this on its update thread. Future work is limited to cheap dirty-state checks here.
        if (!_lifetime.TryGetSession(out _))
            return;

        try
        {
            _gameEventSource?.Poll();
            if (_snapshotSource is null || CurrentGateDecision?.IsEnabled != true)
                return;
            if (_snapshotCoordinator.TryCreateWork(
                    CaptureSnapshot,
                    out var workItem,
                    out var unchangedCompletedState) && workItem is not null)
            {
                EnsureGameSessionStarted(workItem.Snapshot.GameId);
                if (!LocalAdvisorService.IsAnalysisCandidate(workItem.Snapshot))
                {
                    if (_snapshotCoordinator.TryCompleteWork(workItem.StateId))
                    {
                        PublishAdvisorUpdate(PluginAdvisorUpdate.StateOnly(
                            PluginAdvisorStatus.Inactive,
                            workItem.StateId));
                    }
                    return;
                }
                _diagnostics.RecordSnapshot(workItem.Snapshot);
                if (!TryPublishAnalyzing(workItem))
                    return;
                _diagnostics.RecordAdvisorRequest(new AdvisorRequestDiagnostic(
                    workItem.Snapshot.GameId,
                    workItem.StateId));
                SnapshotReady?.Invoke(workItem);
                if (_advisorService is not null)
                    _ = RunAdvisorAsync(workItem);
            }
            else if (unchangedCompletedState)
            {
                RestoreCompletedAdvisorUpdate();
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
        if (_snapshotSource is null)
            return null;
        var captured = _snapshotSource.TryCapture(_gameId, true, out var observation, out var failure);
        if (!captured || observation is null)
        {
            RecordSnapshotCaptureFailure(
                failure == SnapshotCaptureFailure.None ? SnapshotCaptureFailure.EmptyObservation : failure);
            return null;
        }
        _lastRecordedSnapshotCaptureFailure = null;
        return _snapshotBuilder.Build(observation);
    }

    private void HandleGameStarted()
    {
        if (_gameSessionActive)
            _diagnostics.RecordGameEnded(_gameId, completed: false);
        _gameId = Guid.NewGuid();
        _gameSessionActive = true;
        _lastRecordedSnapshotCaptureFailure = null;
        _diagnostics.RecordGameStarted(_gameId);
        HandleStateChanged();
    }

    private void EnsureGameSessionStarted(Guid snapshotGameId)
    {
        if (_gameSessionActive && _gameId == snapshotGameId)
            return;
        if (_gameSessionActive)
            _diagnostics.RecordGameEnded(_gameId, completed: false);
        _gameId = snapshotGameId;
        _gameSessionActive = true;
        _diagnostics.RecordGameStarted(_gameId);
    }

    private void HandleGameEnded()
    {
        if (_gameSessionActive)
        {
            _diagnostics.RecordGameEnded(_gameId, completed: true);
            _gameSessionActive = false;
        }
        _snapshotCoordinator.Reset();
        lock (_advisorStateGate)
            _lastCompletedAdvisorUpdate = null;
        CurrentGateDecision = null;
        _lastRecordedGateStatus = null;
        _lastRecordedSnapshotCaptureFailure = null;
        PublishAdvisorUpdate(PluginAdvisorUpdate.StateOnly(PluginAdvisorStatus.Offline));
    }

    private void HandleStateChanged()
    {
        var stale = PluginAdvisorUpdate.StateOnly(PluginAdvisorStatus.Stale);
        lock (_advisorStateGate)
        {
            _snapshotCoordinator.MarkDirty();
            _currentAdvisorUpdate = stale;
        }
        PublishAutomationAdvice(stale);
        NotifyAdvisorUpdated(stale);
        RefreshEligibility();
    }

    private async Task RunAdvisorAsync(SnapshotWorkItem workItem)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var update = await Task.Run(
                    () => _advisorService!.AnalyzeAsync(workItem.Snapshot, workItem.CancellationToken),
                    workItem.CancellationToken)
                .ConfigureAwait(false);
            if (workItem.CancellationToken.IsCancellationRequested || !TryPublishCompletedAdvisorUpdate(workItem, update))
            {
                stopwatch.Stop();
                RecordAdvisorAnalysis(workItem, update, stopwatch.Elapsed, AdvisorAnalysisDisposition.Superseded);
                return;
            }
            stopwatch.Stop();
            RecordAdvisorAnalysis(workItem, update, stopwatch.Elapsed, AdvisorAnalysisDisposition.Published);
        }
        catch (OperationCanceledException) when (workItem.CancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            var disposition = _snapshotCoordinator.CanAcceptResult(workItem.StateId)
                ? AdvisorAnalysisDisposition.Cancelled
                : AdvisorAnalysisDisposition.Superseded;
            RecordAdvisorAnalysis(workItem, null, stopwatch.Elapsed, disposition);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            RecordAdvisorAnalysis(workItem, null, stopwatch.Elapsed, AdvisorAnalysisDisposition.Failed);
            _diagnostics.RecordError("local_advisor_failed", exception, workItem.StateId);
            TryPublishCurrentState(PluginAdvisorUpdate.StateOnly(PluginAdvisorStatus.Offline, workItem.StateId));
        }
    }

    private bool TryPublishAnalyzing(SnapshotWorkItem workItem)
    {
        var update = PluginAdvisorUpdate.StateOnly(PluginAdvisorStatus.Analyzing, workItem.StateId);
        lock (_advisorStateGate)
        {
            if (workItem.CancellationToken.IsCancellationRequested || !_snapshotCoordinator.CanAcceptResult(workItem.StateId))
                return false;
            _currentAdvisorUpdate = update;
        }
        PublishAutomationAdvice(update);
        NotifyAdvisorUpdated(update);
        return true;
    }

    private bool TryPublishCompletedAdvisorUpdate(SnapshotWorkItem workItem, PluginAdvisorUpdate update)
    {
        lock (_advisorStateGate)
        {
            if (workItem.CancellationToken.IsCancellationRequested || !_snapshotCoordinator.TryCompleteWork(workItem.StateId))
                return false;
            _lastCompletedAdvisorUpdate = update;
            _currentAdvisorUpdate = update;
        }
        PublishAutomationAdvice(update);
        NotifyAdvisorUpdated(update);
        return true;
    }

    private bool TryPublishCurrentState(PluginAdvisorUpdate update)
    {
        lock (_advisorStateGate)
        {
            if (update.StateId is null || !_snapshotCoordinator.CanAcceptResult(update.StateId))
                return false;
            _currentAdvisorUpdate = update;
        }
        PublishAutomationAdvice(update);
        NotifyAdvisorUpdated(update);
        return true;
    }

    private void RestoreCompletedAdvisorUpdate()
    {
        PluginAdvisorUpdate? restored;
        lock (_advisorStateGate)
        {
            restored = _lastCompletedAdvisorUpdate;
            if (restored?.StateId is null ||
                !string.Equals(restored.StateId, _snapshotCoordinator.CurrentStateId, StringComparison.Ordinal))
            {
                return;
            }
            _currentAdvisorUpdate = restored;
        }
        PublishAutomationAdvice(restored);
        NotifyAdvisorUpdated(restored);
    }

    private void RecordAdvisorAnalysis(
        SnapshotWorkItem workItem,
        PluginAdvisorUpdate? update,
        TimeSpan elapsed,
        AdvisorAnalysisDisposition disposition)
    {
        _diagnostics.RecordAdvisorAnalysis(new AdvisorAnalysisDiagnostic(
            workItem.Snapshot.GameId,
            workItem.StateId,
            disposition,
            update?.Status ?? PluginAdvisorStatus.Offline,
            elapsed.TotalMilliseconds,
            update?.Result?.Elapsed.TotalMilliseconds ?? 0,
            update?.Result?.Candidates.Length ?? 0,
            update?.Details.Count ?? 0));
    }

    private void PublishAdvisorUpdate(PluginAdvisorUpdate update)
    {
        lock (_advisorStateGate)
            _currentAdvisorUpdate = update;
        PublishAutomationAdvice(update);
        NotifyAdvisorUpdated(update);
    }

    private void PublishAutomationAdvice(PluginAdvisorUpdate update)
    {
        try
        {
            _automationAdviceSink.Publish(_gameId, update);
        }
        catch (Exception exception)
        {
            _diagnostics.RecordError("automation_advice_publish_failed", exception, update.StateId);
        }
    }

    private void NotifyAdvisorUpdated(PluginAdvisorUpdate update)
    {
        var handlers = AdvisorUpdated;
        if (handlers is null)
            return;
        foreach (Action<PluginAdvisorUpdate> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(update);
            }
            catch (Exception exception)
            {
                _diagnostics.RecordError("advisor_updated_handler_failed", exception, update.StateId);
            }
        }
    }

    private void RecordSnapshotCaptureFailure(SnapshotCaptureFailure failure)
    {
        if (_lastRecordedSnapshotCaptureFailure == failure)
            return;
        _lastRecordedSnapshotCaptureFailure = failure;
        _diagnostics.RecordSnapshotCaptureSkipped(failure);
    }
}
