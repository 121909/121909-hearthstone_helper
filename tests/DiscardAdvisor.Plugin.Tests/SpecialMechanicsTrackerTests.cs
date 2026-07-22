using System;
using DiscardAdvisor.Rules;
using Xunit;

namespace DiscardAdvisor.Plugin.Tests;

public sealed class SpecialMechanicsTrackerTests
{
    [Fact]
    public void BindsNextDrawToPlayedPlatysaurAndPrunesDeadBinding()
    {
        var tracker = new SpecialMechanicsTracker();
        tracker.RecordCardPlayed(DiscardWarlockCardIds.Platysaur, 10);
        tracker.RecordCardDrawn(DiscardWarlockCardIds.HandOfGuldan, 30);

        var living = tracker.Capture(new[] { 30 }, new[] { 10 }, 0, 0);
        var dead = tracker.Capture(new[] { 30 }, Array.Empty<int>(), 0, 0);

        Assert.Equal(30, living.PlatysaurBindings[10]);
        Assert.Empty(dead.PlatysaurBindings);
    }

    [Fact]
    public void SoulariumMarksExactlyNextThreeDrawnEntitiesTemporary()
    {
        var tracker = new SpecialMechanicsTracker();
        tracker.RecordCardPlayed(DiscardWarlockCardIds.Soularium, 10);
        tracker.RecordCardDrawn(DiscardWarlockCardIds.BonewebEgg, 20);
        tracker.RecordCardDrawn(DiscardWarlockCardIds.HandOfGuldan, 21);
        tracker.RecordCardDrawn(DiscardWarlockCardIds.SoulBarrage, 22);
        tracker.RecordCardDrawn(DiscardWarlockCardIds.WalkingDead, 23);

        var state = tracker.Capture(new[] { 20, 21, 22, 23 }, Array.Empty<int>(), 0, 0);

        Assert.Equal(new[] { 20, 21, 22 }, state.TemporaryEntityIds);
    }

    [Fact]
    public void CursedCatacombsMarksOnlyActualCreatedEntityTemporary()
    {
        var tracker = new SpecialMechanicsTracker();
        tracker.RecordCardPlayed(DiscardWarlockCardIds.CursedCatacombs, 10);
        tracker.RecordCardCreatedInHand(40);
        tracker.RecordCardCreatedInHand(41);

        var state = tracker.Capture(new[] { 40, 41 }, Array.Empty<int>(), 0, 0);

        Assert.Equal(new[] { 40 }, state.TemporaryEntityIds);
    }

    [Fact]
    public void StableCaptureClearsUnresolvedOneShotExpectations()
    {
        var platysaur = new SpecialMechanicsTracker();
        platysaur.RecordCardPlayed(DiscardWarlockCardIds.Platysaur, 10);
        platysaur.Capture(Array.Empty<int>(), new[] { 10 }, 0, 0);
        platysaur.RecordCardDrawn(DiscardWarlockCardIds.HandOfGuldan, 20);

        var soularium = new SpecialMechanicsTracker();
        soularium.RecordCardPlayed(DiscardWarlockCardIds.Soularium, 11);
        soularium.RecordCardDrawn(DiscardWarlockCardIds.BonewebEgg, 21);
        var soulariumStable = soularium.Capture(new[] { 21 }, Array.Empty<int>(), 0, 0);
        soularium.RecordCardDrawn(DiscardWarlockCardIds.HandOfGuldan, 22);

        var catacombs = new SpecialMechanicsTracker();
        catacombs.RecordCardPlayed(DiscardWarlockCardIds.CursedCatacombs, 12);
        catacombs.Capture(Array.Empty<int>(), Array.Empty<int>(), 0, 0);
        catacombs.RecordCardCreatedInHand(23);

        Assert.Empty(platysaur.Capture(new[] { 20 }, new[] { 10 }, 0, 0).PlatysaurBindings);
        Assert.Equal(new[] { 21 }, soulariumStable.TemporaryEntityIds);
        Assert.Equal(new[] { 21 }, soularium.Capture(new[] { 21, 22 }, Array.Empty<int>(), 0, 0).TemporaryEntityIds);
        Assert.Empty(catacombs.Capture(new[] { 23 }, Array.Empty<int>(), 0, 0).TemporaryEntityIds);
    }

    [Fact]
    public void DiscardRemovesTemporaryEntityAndKeepsMonotonicCount()
    {
        var tracker = new SpecialMechanicsTracker();
        tracker.RecordCardPlayed(DiscardWarlockCardIds.CursedCatacombs, 10);
        tracker.RecordCardCreatedInHand(40);
        tracker.RecordCardDiscarded(40);

        var state = tracker.Capture(Array.Empty<int>(), Array.Empty<int>(), 0, 0);
        var observedHigherCount = tracker.Capture(Array.Empty<int>(), Array.Empty<int>(), 3, 0);

        Assert.Empty(state.TemporaryEntityIds);
        Assert.Equal(1, state.DiscardCount);
        Assert.Equal(3, observedHigherCount.DiscardCount);
    }

    [Fact]
    public void TracksShredsFromShuffleAndDrawUntilObservedDeckReconciles()
    {
        var tracker = new SpecialMechanicsTracker();
        tracker.RecordCardPlayed(DiscardWarlockCardIds.EntropicContinuity, 10);
        var shuffled = tracker.Capture(Array.Empty<int>(), Array.Empty<int>(), 0, -1);
        tracker.RecordCardDrawn(DiscardWarlockCardIds.ShredOfTime, 50);
        var drawn = tracker.Capture(Array.Empty<int>(), Array.Empty<int>(), 0, -1);
        var reconciled = tracker.Capture(Array.Empty<int>(), Array.Empty<int>(), 0, 4);

        Assert.Equal(2, shuffled.ShredsOfTimeInDeck);
        Assert.Equal(1, drawn.ShredsOfTimeInDeck);
        Assert.Equal(4, reconciled.ShredsOfTimeInDeck);
    }

    [Fact]
    public void CapturesLocationDurabilityCooldownAndAvailability()
    {
        var tracker = new SpecialMechanicsTracker();
        tracker.RecordLocation(60, 2, 0, true);
        tracker.RecordLocation(60, 1, 2, false);

        var state = tracker.Capture(Array.Empty<int>(), Array.Empty<int>(), 0, 0);
        var location = Assert.Single(state.Locations).Value;

        Assert.Equal((1, 2, false), (location.Durability, location.Cooldown, location.Available));
    }

    [Fact]
    public void CaptureReturnsDetachedReadOnlyCollections()
    {
        var tracker = new SpecialMechanicsTracker();
        tracker.RecordCardPlayed(DiscardWarlockCardIds.Soularium, 10);
        tracker.RecordCardDrawn(DiscardWarlockCardIds.BonewebEgg, 20);
        var first = tracker.Capture(new[] { 20 }, Array.Empty<int>(), 0, 0);

        tracker.RecordCardLeftHand(20);
        var second = tracker.Capture(Array.Empty<int>(), Array.Empty<int>(), 0, 0);

        Assert.Equal(new[] { 20 }, first.TemporaryEntityIds);
        Assert.Empty(second.TemporaryEntityIds);
    }
}
