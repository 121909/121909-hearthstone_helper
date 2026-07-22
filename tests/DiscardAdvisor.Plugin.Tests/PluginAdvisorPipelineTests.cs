using System;
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
        var statuses = new System.Collections.Generic.List<PluginAdvisorStatus>();
        runtime.AdvisorUpdated += update => statuses.Add(update.Status);

        runtime.Start();
        runtime.Update();

        Assert.Equal(PluginAdvisorStatus.Analyzing, runtime.CurrentAdvisorUpdate.Status);
        Assert.NotNull(advisor.Snapshot);
        advisor.Complete(PluginAdvisorUpdate.StateOnly(
            PluginAdvisorStatus.NoLegalRoute,
            advisor.Snapshot!.StateId));
        await WaitUntilAsync(() => runtime.CurrentAdvisorUpdate.Status == PluginAdvisorStatus.NoLegalRoute);

        Assert.Contains(PluginAdvisorStatus.Analyzing, statuses);
        Assert.Equal(PluginAdvisorStatus.NoLegalRoute, runtime.CurrentAdvisorUpdate.Status);
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

        public Task<PluginAdvisorUpdate> AnalyzeAsync(GameSnapshot snapshot, CancellationToken cancellationToken)
        {
            Snapshot = snapshot;
            cancellationToken.Register(() => _completion.TrySetCanceled(cancellationToken));
            return _completion.Task;
        }

        public void Complete(PluginAdvisorUpdate update) => _completion.TrySetResult(update);
    }
}
