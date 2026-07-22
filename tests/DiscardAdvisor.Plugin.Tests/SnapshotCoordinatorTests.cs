using System;
using DiscardAdvisor.Domain.Snapshots;
using Xunit;

namespace DiscardAdvisor.Plugin.Tests;

public sealed class SnapshotCoordinatorTests
{
    [Fact]
    public void WaitsForStableWindowBeforeCapturing()
    {
        var now = DateTimeOffset.Parse("2026-07-22T00:00:00Z");
        using var coordinator = new SnapshotCoordinator(() => now, TimeSpan.FromMilliseconds(200));
        var captures = 0;
        GameSnapshot Capture()
        {
            captures++;
            return CreateSnapshot(3);
        }

        coordinator.MarkDirty();

        now = now.AddMilliseconds(199);
        Assert.False(coordinator.TryCreateWork(Capture, out _));
        Assert.Equal(0, captures);

        now = now.AddMilliseconds(1);
        Assert.True(coordinator.TryCreateWork(Capture, out var work));
        Assert.Equal(1, captures);
        Assert.NotNull(work);
    }

    [Fact]
    public void DirtyStateCancelsOldWorkAndRejectsItsResult()
    {
        var now = DateTimeOffset.Parse("2026-07-22T00:00:00Z");
        using var coordinator = new SnapshotCoordinator(() => now, TimeSpan.FromMilliseconds(200));
        coordinator.MarkDirty();
        now = now.AddMilliseconds(200);
        Assert.True(coordinator.TryCreateWork(() => CreateSnapshot(3), out var work));
        Assert.NotNull(work);
        Assert.True(coordinator.CanAcceptResult(work!.StateId));

        coordinator.MarkDirty();

        Assert.True(work.CancellationToken.IsCancellationRequested);
        Assert.False(coordinator.CanAcceptResult(work.StateId));
    }

    [Fact]
    public void DoesNotDispatchAnUnchangedCompletedStateTwice()
    {
        var now = DateTimeOffset.Parse("2026-07-22T00:00:00Z");
        using var coordinator = new SnapshotCoordinator(() => now, TimeSpan.FromMilliseconds(200));
        coordinator.MarkDirty();
        now = now.AddMilliseconds(200);
        Assert.True(coordinator.TryCreateWork(() => CreateSnapshot(3), out var first));
        Assert.True(coordinator.TryCompleteWork(first!.StateId));

        coordinator.MarkDirty();
        now = now.AddMilliseconds(200);
        Assert.False(coordinator.TryCreateWork(() => CreateSnapshot(3), out var duplicate, out var unchanged));

        Assert.Null(duplicate);
        Assert.True(unchanged);
        Assert.True(coordinator.CanAcceptResult(first.StateId));
    }

    [Fact]
    public void RedispatchesAnUnchangedStateWhenPriorWorkWasCancelled()
    {
        var now = DateTimeOffset.Parse("2026-07-22T00:00:00Z");
        using var coordinator = new SnapshotCoordinator(() => now, TimeSpan.FromMilliseconds(200));
        coordinator.MarkDirty();
        now = now.AddMilliseconds(200);
        Assert.True(coordinator.TryCreateWork(() => CreateSnapshot(3), out var first));

        coordinator.MarkDirty();
        now = now.AddMilliseconds(200);
        Assert.True(coordinator.TryCreateWork(() => CreateSnapshot(3), out var second, out var unchanged));

        Assert.True(first!.CancellationToken.IsCancellationRequested);
        Assert.False(unchanged);
        Assert.NotNull(second);
        Assert.Equal(first.StateId, second!.StateId);
    }

    [Fact]
    public void DispatchesAChangedStateWithANewId()
    {
        var now = DateTimeOffset.Parse("2026-07-22T00:00:00Z");
        using var coordinator = new SnapshotCoordinator(() => now, TimeSpan.FromMilliseconds(200));
        coordinator.MarkDirty();
        now = now.AddMilliseconds(200);
        Assert.True(coordinator.TryCreateWork(() => CreateSnapshot(3), out var first));

        coordinator.MarkDirty();
        now = now.AddMilliseconds(200);
        Assert.True(coordinator.TryCreateWork(() => CreateSnapshot(2), out var second));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.StateId, second!.StateId);
    }

    private static GameSnapshot CreateSnapshot(int availableMana)
    {
        var friendly = GameSnapshotBuilderTests.CreateFriendly(Array.Empty<HandCardSnapshot>(), availableMana);
        return new GameSnapshotBuilder().Build(GameSnapshotBuilderTests.CreateObservation(friendly));
    }
}
