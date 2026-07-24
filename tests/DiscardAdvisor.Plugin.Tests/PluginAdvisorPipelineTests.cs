using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiscardAdvisor.Domain;
using DiscardAdvisor.Domain.Snapshots;
using DiscardAdvisor.Search;
using Xunit;

namespace DiscardAdvisor.Plugin.Tests;

public sealed class PluginAdvisorPipelineTests
{
    [Fact]
    public async Task AdvisorSubscriberFailureDoesNotTurnCompletedAnalysisIntoFailure()
    {
        var observation = GameSnapshotBuilderTests.CreateObservation(
            GameSnapshotBuilderTests.CreateFriendly(Array.Empty<HandCardSnapshot>()));
        var source = new StubSnapshotSource(observation);
        var events = new TriggerGameEventSource();
        var advisor = new ControlledAdvisorService();
        var diagnostics = new RecordingDiagnostics();
        using var coordinator = new SnapshotCoordinator(() => DateTimeOffset.UtcNow, TimeSpan.Zero);
        using var runtime = new PluginRuntime(
            new PluginLifetime(),
            new StubContextProvider(SupportedContext()),
            events,
            source,
            snapshotCoordinator: coordinator,
            diagnostics: diagnostics,
            advisorService: advisor);
        runtime.AdvisorUpdated += _ => throw new InvalidOperationException("overlay failed");

        runtime.Start();
        runtime.Update();
        await WaitUntilAsync(() => advisor.Snapshot is not null);
        advisor.Complete(PluginAdvisorUpdate.StateOnly(
            PluginAdvisorStatus.NoLegalRoute,
            advisor.Snapshot!.StateId));
        await WaitUntilAsync(() => diagnostics.Analyses.Any());

        Assert.Equal(AdvisorAnalysisDisposition.Published, Assert.Single(diagnostics.Analyses).Disposition);
        Assert.DoesNotContain(diagnostics.Analyses, analysis => analysis.Disposition == AdvisorAnalysisDisposition.Failed);
    }

    [Fact]
    public async Task LocalAdvisorServiceMapsSnapshotAndReturnsCandidates()
    {
        var snapshot = new GameSnapshotBuilder().Build(GameSnapshotBuilderTests.CreateObservation(
            GameSnapshotBuilderTests.CreateFriendly(Array.Empty<HandCardSnapshot>())));
        var service = new LocalAdvisorService(
            new LocalTurnAdvisor(),
            options: new LocalAdvisorOptions(new BeamSearchOptions(
                BeamWidth: 16,
                MaximumActions: 2,
                TopK: 3,
                TimeBudget: TimeSpan.FromSeconds(1))));

        var update = await service.AnalyzeAsync(snapshot, CancellationToken.None);

        Assert.Equal(PluginAdvisorStatus.Ready, update.Status);
        Assert.Equal(snapshot.StateId, update.StateId);
        Assert.NotNull(update.State);
        Assert.NotNull(update.Result);
        Assert.NotEmpty(update.Result!.Candidates);
    }

    [Fact]
    public async Task LocalAdvisorServiceIsInactiveOutsideFriendlyMainAction()
    {
        var snapshot = new GameSnapshotBuilder().Build(GameSnapshotBuilderTests.CreateObservation(
            GameSnapshotBuilderTests.CreateFriendly(Array.Empty<HandCardSnapshot>())));
        var opponentTurn = WithTurnContext(snapshot, "OPPONENT", "MAIN_ACTION", true);
        var wrongStep = WithTurnContext(snapshot, "FRIENDLY", "MAIN_END", true);
        var unstable = WithTurnContext(snapshot, "FRIENDLY", "MAIN_ACTION", false);
        var service = new LocalAdvisorService(new LocalTurnAdvisor());

        var updates = await Task.WhenAll(
            service.AnalyzeAsync(opponentTurn, CancellationToken.None),
            service.AnalyzeAsync(wrongStep, CancellationToken.None),
            service.AnalyzeAsync(unstable, CancellationToken.None));

        Assert.All(updates, update =>
        {
            Assert.Equal(PluginAdvisorStatus.Inactive, update.Status);
            Assert.Null(update.Result);
        });
    }

    [Fact]
    public async Task RuntimePublishesAnalyzingThenAcceptsCurrentAdvisorResult()
    {
        var observation = GameSnapshotBuilderTests.CreateObservation(
            GameSnapshotBuilderTests.CreateFriendly(Array.Empty<HandCardSnapshot>()));
        var source = new StubSnapshotSource(observation);
        var events = new TriggerGameEventSource();
        var advisor = new ControlledAdvisorService();
        var diagnostics = new RecordingDiagnostics();
        using var coordinator = new SnapshotCoordinator(() => DateTimeOffset.UtcNow, TimeSpan.Zero);
        using var runtime = new PluginRuntime(
            new PluginLifetime(),
            new StubContextProvider(SupportedContext()),
            events,
            source,
            snapshotCoordinator: coordinator,
            diagnostics: diagnostics,
            advisorService: advisor);
        var statuses = new ConcurrentQueue<PluginAdvisorStatus>();
        runtime.AdvisorUpdated += update => statuses.Enqueue(update.Status);
        var updateThreadId = 0;

        var updateThread = new Thread(() =>
        {
            updateThreadId = Environment.CurrentManagedThreadId;
            runtime.Start();
            runtime.Update();
        });
        updateThread.Start();
        updateThread.Join();

        Assert.Equal(PluginAdvisorStatus.Analyzing, runtime.CurrentAdvisorUpdate.Status);
        Assert.Equal(1, events.PollCount);
        await WaitUntilAsync(() => advisor.Snapshot is not null);
        Assert.Equal(advisor.Snapshot!.GameId, Assert.Single(diagnostics.StartedGames));
        Assert.Equal(advisor.Snapshot.StateId, Assert.Single(diagnostics.Requests).StateId);
        Assert.NotEqual(updateThreadId, advisor.InvocationThreadId);
        advisor.Complete(PluginAdvisorUpdate.StateOnly(
            PluginAdvisorStatus.NoLegalRoute,
            advisor.Snapshot!.StateId));
        await WaitUntilAsync(() => runtime.CurrentAdvisorUpdate.Status == PluginAdvisorStatus.NoLegalRoute);

        Assert.Contains(PluginAdvisorStatus.Analyzing, statuses);
        Assert.Equal(PluginAdvisorStatus.NoLegalRoute, runtime.CurrentAdvisorUpdate.Status);
        Assert.Equal(1, advisor.InvocationCount);

        events.TriggerStateChanged();
        runtime.Update();

        Assert.Equal(PluginAdvisorStatus.NoLegalRoute, runtime.CurrentAdvisorUpdate.Status);
        Assert.Equal(1, advisor.InvocationCount);
        Assert.Equal(2, events.PollCount);
        Assert.Single(diagnostics.Requests);
    }

    [Fact]
    public void RuntimeDoesNotExportOrDispatchSnapshotsOutsideFriendlyMainAction()
    {
        var original = GameSnapshotBuilderTests.CreateObservation(
            GameSnapshotBuilderTests.CreateFriendly(Array.Empty<HandCardSnapshot>()));
        var observation = new GameObservation(
            original.HearthstoneBuild,
            original.HdtVersion,
            original.CardDefsHash,
            original.GameId,
            original.TurnNumber,
            "MAIN_START",
            "OPPONENT",
            original.RemainingTurnTimeMs,
            original.IsStable,
            original.Friendly,
            original.Opponent,
            original.Derived,
            original.SensitiveMetadata,
            original.ActionsThisTurn,
            original.CurrentChoice);
        var diagnostics = new RecordingDiagnostics();
        var advisor = new ControlledAdvisorService();
        using var coordinator = new SnapshotCoordinator(() => DateTimeOffset.UtcNow, TimeSpan.Zero);
        using var runtime = new PluginRuntime(
            new PluginLifetime(),
            new StubContextProvider(SupportedContext()),
            new TriggerGameEventSource(),
            new StubSnapshotSource(observation),
            coordinator,
            diagnostics,
            advisor);

        runtime.Start();
        runtime.Update();

        Assert.Equal(PluginAdvisorStatus.Inactive, runtime.CurrentAdvisorUpdate.Status);
        Assert.Equal(0, advisor.InvocationCount);
        Assert.Empty(diagnostics.Snapshots);
        Assert.Empty(diagnostics.Requests);
        Assert.Empty(diagnostics.Analyses);
    }

    [Fact]
    public async Task LateResultFromCancelledStateIsNeverPublished()
    {
        var firstObservation = GameSnapshotBuilderTests.CreateObservation(
            GameSnapshotBuilderTests.CreateFriendly(Array.Empty<HandCardSnapshot>(), 3));
        var secondObservation = GameSnapshotBuilderTests.CreateObservation(
            GameSnapshotBuilderTests.CreateFriendly(Array.Empty<HandCardSnapshot>(), 2));
        var source = new SequencedSnapshotSource(firstObservation, secondObservation);
        var events = new TriggerGameEventSource();
        var advisor = new IgnoringCancellationAdvisorService();
        var diagnostics = new RecordingDiagnostics();
        using var coordinator = new SnapshotCoordinator(() => DateTimeOffset.UtcNow, TimeSpan.Zero);
        using var runtime = new PluginRuntime(
            new PluginLifetime(),
            new StubContextProvider(SupportedContext()),
            events,
            source,
            coordinator,
            diagnostics,
            advisorService: advisor);

        runtime.Start();
        runtime.Update();
        await WaitUntilAsync(() => advisor.Count == 1);
        var firstStateId = advisor.StateId(0);
        var firstGameId = advisor.GameId(0);

        events.TriggerGameStarted();
        runtime.Update();
        await WaitUntilAsync(() => advisor.Count == 2);
        var secondStateId = advisor.StateId(1);
        var secondGameId = advisor.GameId(1);
        Assert.NotEqual(firstStateId, secondStateId);
        Assert.NotEqual(firstGameId, secondGameId);

        advisor.Complete(0, PluginAdvisorUpdate.StateOnly(PluginAdvisorStatus.NoLegalRoute, firstStateId));
        await Task.Delay(50);
        Assert.Equal(PluginAdvisorStatus.Analyzing, runtime.CurrentAdvisorUpdate.Status);
        Assert.Equal(secondStateId, runtime.CurrentAdvisorUpdate.StateId);

        advisor.Complete(1, PluginAdvisorUpdate.StateOnly(PluginAdvisorStatus.NoLegalRoute, secondStateId));
        await WaitUntilAsync(() => runtime.CurrentAdvisorUpdate.Status == PluginAdvisorStatus.NoLegalRoute);
        Assert.Equal(secondStateId, runtime.CurrentAdvisorUpdate.StateId);
        await WaitUntilAsync(() => diagnostics.Analyses.Count == 2);
        Assert.Contains(diagnostics.Analyses, analysis =>
            analysis.StateId == firstStateId &&
            analysis.GameId == firstGameId &&
            analysis.Disposition == AdvisorAnalysisDisposition.Superseded);
        Assert.Contains(diagnostics.Analyses, analysis =>
            analysis.StateId == secondStateId &&
            analysis.GameId == secondGameId &&
            analysis.Disposition == AdvisorAnalysisDisposition.Published);
    }

    [Fact]
    public async Task UnchangedStateIsRedispatchedWhenPriorAnalysisWasCancelled()
    {
        var observation = GameSnapshotBuilderTests.CreateObservation(
            GameSnapshotBuilderTests.CreateFriendly(Array.Empty<HandCardSnapshot>()));
        var source = new SequencedSnapshotSource(observation, observation);
        var events = new TriggerGameEventSource();
        var advisor = new IgnoringCancellationAdvisorService();
        using var coordinator = new SnapshotCoordinator(() => DateTimeOffset.UtcNow, TimeSpan.Zero);
        using var runtime = new PluginRuntime(
            new PluginLifetime(),
            new StubContextProvider(SupportedContext()),
            events,
            source,
            coordinator,
            advisorService: advisor);

        runtime.Start();
        runtime.Update();
        await WaitUntilAsync(() => advisor.Count == 1);
        var stateId = advisor.StateId(0);

        events.TriggerStateChanged();
        runtime.Update();
        await WaitUntilAsync(() => advisor.Count == 2);
        Assert.Equal(stateId, advisor.StateId(1));

        advisor.Complete(0, PluginAdvisorUpdate.StateOnly(PluginAdvisorStatus.NoLegalRoute, stateId));
        await Task.Delay(50);
        Assert.Equal(PluginAdvisorStatus.Analyzing, runtime.CurrentAdvisorUpdate.Status);
        advisor.Complete(1, PluginAdvisorUpdate.StateOnly(PluginAdvisorStatus.NoLegalRoute, stateId));
        await WaitUntilAsync(() => runtime.CurrentAdvisorUpdate.Status == PluginAdvisorStatus.NoLegalRoute);
    }

    [Fact]
    public void RuntimeRecordsGameSessionBoundaries()
    {
        var events = new TriggerGameEventSource();
        var diagnostics = new RecordingDiagnostics();
        using var runtime = new PluginRuntime(
            new PluginLifetime(),
            new StubContextProvider(SupportedContext()),
            events,
            snapshotSource: null,
            diagnostics: diagnostics);

        runtime.Start();
        events.TriggerGameStarted();
        events.TriggerGameEnded();

        var ended = Assert.Single(diagnostics.EndedGames);
        Assert.Equal(Assert.Single(diagnostics.StartedGames), ended.GameId);
        Assert.True(ended.Completed);
    }

    [Fact]
    public void RuntimeRecordsUnavailableSnapshotReasonWithoutSuppressingRetries()
    {
        var source = new UnavailableSnapshotSource(SnapshotCaptureFailure.MissingOpponentHeroPower);
        var diagnostics = new RecordingDiagnostics();
        using var coordinator = new SnapshotCoordinator(() => DateTimeOffset.UtcNow, TimeSpan.Zero);
        using var runtime = new PluginRuntime(
            new PluginLifetime(),
            new StubContextProvider(SupportedContext()),
            new TriggerGameEventSource(),
            source,
            coordinator,
            diagnostics);

        runtime.Start();
        runtime.Update();
        runtime.Update();

        Assert.Equal(
            new[] { SnapshotCaptureFailure.MissingOpponentHeroPower },
            diagnostics.CaptureFailures.ToArray());

        source.Failure = SnapshotCaptureFailure.MissingPlayerEntity;
        runtime.Update();

        Assert.Equal(
            new[]
            {
                SnapshotCaptureFailure.MissingOpponentHeroPower,
                SnapshotCaptureFailure.MissingPlayerEntity
            },
            diagnostics.CaptureFailures.ToArray());
    }

    [Fact]
    public void UnsupportedCompatibilityPublishesUnsupportedPatch()
    {
        var context = SupportedContext();
        var unsupported = new PluginGateContext(
            context.GameMode,
            context.DeckCardIds,
            new RuntimeCompatibility(
                TargetRuntimeCompatibility.HearthstoneBuild + 1,
                TargetRuntimeCompatibility.HdtVersion,
                TargetRuntimeCompatibility.CardDefsSha256,
                TargetRuntimeCompatibility.HearthDbSha256));
        using var runtime = new PluginRuntime(new StubContextProvider(unsupported));

        runtime.Start();

        Assert.Equal(PluginAdvisorStatus.UnsupportedPatch, runtime.CurrentAdvisorUpdate.Status);
    }

    private static PluginGateContext SupportedContext()
    {
        var cards = TargetDeckProfile.Cards.SelectMany(card =>
            Enumerable.Repeat<string?>(card.CardId, card.Count));
        return new PluginGateContext(
            TargetDeckProfile.GameMode,
            cards,
            new RuntimeCompatibility(
                TargetRuntimeCompatibility.HearthstoneBuild,
                TargetRuntimeCompatibility.HdtVersion,
                TargetRuntimeCompatibility.CardDefsSha256,
                TargetRuntimeCompatibility.HearthDbSha256));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class StubContextProvider : IGameContextProvider
    {
        private readonly PluginGateContext _context;

        public StubContextProvider(PluginGateContext context)
        {
            _context = context;
        }

        public PluginGateContext CaptureGateContext() => _context;
    }

    private sealed class StubSnapshotSource : ISnapshotObservationSource
    {
        private readonly GameObservation _observation;

        public StubSnapshotSource(GameObservation observation)
        {
            _observation = observation;
        }

        public bool TryCapture(
            Guid gameId,
            bool isStable,
            out GameObservation? observation,
            out SnapshotCaptureFailure failure)
        {
            observation = WithGameId(_observation, gameId);
            failure = SnapshotCaptureFailure.None;
            return true;
        }
    }

    private sealed class UnavailableSnapshotSource : ISnapshotObservationSource
    {
        public UnavailableSnapshotSource(SnapshotCaptureFailure failure)
        {
            Failure = failure;
        }

        public SnapshotCaptureFailure Failure { get; set; }

        public bool TryCapture(
            Guid gameId,
            bool isStable,
            out GameObservation? observation,
            out SnapshotCaptureFailure failure)
        {
            observation = null;
            failure = Failure;
            return false;
        }
    }

    private sealed class ControlledAdvisorService : ILocalAdvisorService
    {
        private readonly TaskCompletionSource<PluginAdvisorUpdate> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GameSnapshot? Snapshot { get; private set; }
        public int InvocationThreadId { get; private set; }
        public int InvocationCount { get; private set; }

        public Task<PluginAdvisorUpdate> AnalyzeAsync(GameSnapshot snapshot, CancellationToken cancellationToken)
        {
            InvocationThreadId = Environment.CurrentManagedThreadId;
            InvocationCount++;
            Snapshot = snapshot;
            cancellationToken.Register(() => _completion.TrySetCanceled(cancellationToken));
            return _completion.Task;
        }

        public void Complete(PluginAdvisorUpdate update) => _completion.TrySetResult(update);
    }

    private sealed class SequencedSnapshotSource : ISnapshotObservationSource
    {
        private readonly Queue<GameObservation> _observations;

        public SequencedSnapshotSource(params GameObservation[] observations)
        {
            _observations = new Queue<GameObservation>(observations);
        }

        public bool TryCapture(
            Guid gameId,
            bool isStable,
            out GameObservation? observation,
            out SnapshotCaptureFailure failure)
        {
            observation = _observations.Count > 0 ? WithGameId(_observations.Dequeue(), gameId) : null;
            failure = observation is null ? SnapshotCaptureFailure.EmptyObservation : SnapshotCaptureFailure.None;
            return observation is not null;
        }
    }

    private static GameObservation WithGameId(GameObservation observation, Guid gameId) => new(
        observation.HearthstoneBuild,
        observation.HdtVersion,
        observation.CardDefsHash,
        gameId,
        observation.TurnNumber,
        observation.Step,
        observation.ActivePlayer,
        observation.RemainingTurnTimeMs,
        observation.IsStable,
        observation.Friendly,
        observation.Opponent,
        observation.Derived,
        observation.SensitiveMetadata,
        observation.ActionsThisTurn,
        observation.CurrentChoice);

    private static GameSnapshot WithTurnContext(
        GameSnapshot snapshot,
        string activePlayer,
        string step,
        bool isStable) => new(
        snapshot.RuleSetVersion,
        snapshot.HearthstoneBuild,
        snapshot.HdtVersion,
        snapshot.CardDefsHash,
        snapshot.GameId,
        snapshot.StateId,
        snapshot.TurnNumber,
        step,
        activePlayer,
        snapshot.RemainingTurnTimeMs,
        isStable,
        snapshot.Friendly,
        snapshot.Opponent,
        snapshot.ActionsThisTurn,
        snapshot.Derived,
        snapshot.CurrentChoice);

    private sealed class TriggerGameEventSource : IGameEventSource
    {
        private Action? _gameStarted;
        private Action? _gameEnded;
        private Action? _stateChanged;

        public int PollCount { get; private set; }

        public void Start(Action gameStarted, Action gameEnded, Action stateChanged)
        {
            _gameStarted = gameStarted;
            _gameEnded = gameEnded;
            _stateChanged = stateChanged;
        }

        public void Stop()
        {
            _gameStarted = null;
            _gameEnded = null;
            _stateChanged = null;
        }

        public void Poll()
        {
            PollCount++;
        }

        public void TriggerGameStarted() => _gameStarted?.Invoke();

        public void TriggerGameEnded() => _gameEnded?.Invoke();

        public void TriggerStateChanged() => _stateChanged?.Invoke();
    }

    private sealed class RecordingDiagnostics : IPluginDiagnostics
    {
        public ConcurrentQueue<Guid> StartedGames { get; } = new();

        public ConcurrentQueue<(Guid GameId, bool Completed)> EndedGames { get; } = new();

        public ConcurrentQueue<AdvisorAnalysisDiagnostic> Analyses { get; } = new();

        public ConcurrentQueue<AdvisorRequestDiagnostic> Requests { get; } = new();

        public ConcurrentQueue<SnapshotCaptureFailure> CaptureFailures { get; } = new();

        public ConcurrentQueue<GameSnapshot> Snapshots { get; } = new();

        public void RecordGameStarted(Guid gameId) => StartedGames.Enqueue(gameId);

        public void RecordGameEnded(Guid gameId, bool completed) => EndedGames.Enqueue((gameId, completed));

        public void RecordGateDecision(GateDecision decision)
        {
        }

        public void RecordSnapshotCaptureSkipped(SnapshotCaptureFailure failure) => CaptureFailures.Enqueue(failure);

        public void RecordSnapshot(GameSnapshot snapshot) => Snapshots.Enqueue(snapshot);

        public void RecordAdvisorRequest(AdvisorRequestDiagnostic request) => Requests.Enqueue(request);

        public void RecordAdvisorAnalysis(AdvisorAnalysisDiagnostic analysis) => Analyses.Enqueue(analysis);

        public void RecordError(string code, Exception exception, string? stateId = null)
        {
        }
    }

    private sealed class IgnoringCancellationAdvisorService : ILocalAdvisorService
    {
        private readonly object _gate = new();
        private readonly List<(GameSnapshot Snapshot, TaskCompletionSource<PluginAdvisorUpdate> Completion)> _calls = new();

        public int Count
        {
            get
            {
                lock (_gate)
                    return _calls.Count;
            }
        }

        public Task<PluginAdvisorUpdate> AnalyzeAsync(GameSnapshot snapshot, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<PluginAdvisorUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
                _calls.Add((snapshot, completion));
            return completion.Task;
        }

        public string StateId(int index)
        {
            lock (_gate)
                return _calls[index].Snapshot.StateId;
        }

        public Guid GameId(int index)
        {
            lock (_gate)
                return _calls[index].Snapshot.GameId;
        }

        public void Complete(int index, PluginAdvisorUpdate update)
        {
            TaskCompletionSource<PluginAdvisorUpdate> completion;
            lock (_gate)
                completion = _calls[index].Completion;
            completion.TrySetResult(update);
        }
    }
}
