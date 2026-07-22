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
    public async Task RuntimePublishesAnalyzingThenAcceptsCurrentAdvisorResult()
    {
        var observation = GameSnapshotBuilderTests.CreateObservation(
            GameSnapshotBuilderTests.CreateFriendly(Array.Empty<HandCardSnapshot>()));
        var source = new StubSnapshotSource(observation);
        var advisor = new ControlledAdvisorService();
        using var coordinator = new SnapshotCoordinator(() => DateTimeOffset.UtcNow, TimeSpan.Zero);
        using var runtime = new PluginRuntime(
            new PluginLifetime(),
            new StubContextProvider(SupportedContext()),
            snapshotSource: source,
            snapshotCoordinator: coordinator,
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
        await WaitUntilAsync(() => advisor.Snapshot is not null);
        Assert.NotEqual(updateThreadId, advisor.InvocationThreadId);
        advisor.Complete(PluginAdvisorUpdate.StateOnly(
            PluginAdvisorStatus.NoLegalRoute,
            advisor.Snapshot!.StateId));
        await WaitUntilAsync(() => runtime.CurrentAdvisorUpdate.Status == PluginAdvisorStatus.NoLegalRoute);

        Assert.Contains(PluginAdvisorStatus.Analyzing, statuses);
        Assert.Equal(PluginAdvisorStatus.NoLegalRoute, runtime.CurrentAdvisorUpdate.Status);
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
        var firstStateId = advisor.StateId(0);

        events.TriggerStateChanged();
        runtime.Update();
        await WaitUntilAsync(() => advisor.Count == 2);
        var secondStateId = advisor.StateId(1);
        Assert.NotEqual(firstStateId, secondStateId);

        advisor.Complete(0, PluginAdvisorUpdate.StateOnly(PluginAdvisorStatus.NoLegalRoute, firstStateId));
        await Task.Delay(50);
        Assert.Equal(PluginAdvisorStatus.Analyzing, runtime.CurrentAdvisorUpdate.Status);
        Assert.Equal(secondStateId, runtime.CurrentAdvisorUpdate.StateId);

        advisor.Complete(1, PluginAdvisorUpdate.StateOnly(PluginAdvisorStatus.NoLegalRoute, secondStateId));
        await WaitUntilAsync(() => runtime.CurrentAdvisorUpdate.Status == PluginAdvisorStatus.NoLegalRoute);
        Assert.Equal(secondStateId, runtime.CurrentAdvisorUpdate.StateId);
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

        public bool TryCapture(Guid gameId, bool isStable, out GameObservation? observation)
        {
            observation = _observation;
            return true;
        }
    }

    private sealed class ControlledAdvisorService : ILocalAdvisorService
    {
        private readonly TaskCompletionSource<PluginAdvisorUpdate> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GameSnapshot? Snapshot { get; private set; }
        public int InvocationThreadId { get; private set; }

        public Task<PluginAdvisorUpdate> AnalyzeAsync(GameSnapshot snapshot, CancellationToken cancellationToken)
        {
            InvocationThreadId = Environment.CurrentManagedThreadId;
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

        public bool TryCapture(Guid gameId, bool isStable, out GameObservation? observation)
        {
            observation = _observations.Count > 0 ? _observations.Dequeue() : null;
            return observation is not null;
        }
    }

    private sealed class TriggerGameEventSource : IGameEventSource
    {
        private Action? _stateChanged;

        public void Start(Action gameStarted, Action gameEnded, Action stateChanged)
        {
            _stateChanged = stateChanged;
        }

        public void Stop()
        {
            _stateChanged = null;
        }

        public void TriggerStateChanged() => _stateChanged?.Invoke();
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

        public void Complete(int index, PluginAdvisorUpdate update)
        {
            TaskCompletionSource<PluginAdvisorUpdate> completion;
            lock (_gate)
                completion = _calls[index].Completion;
            completion.TrySetResult(update);
        }
    }
}
