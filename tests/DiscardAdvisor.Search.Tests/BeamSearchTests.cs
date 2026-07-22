using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using DiscardAdvisor.Rules;
using DiscardAdvisor.Rules.Model;
using Xunit;

namespace DiscardAdvisor.Search.Tests;

public sealed class BeamSearchTests
{
    [Fact]
    public void PrioritizesDeterministicLethalRoute()
    {
        var soulfire = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.Soulfire, 10);
        var state = CreateState(new[] { soulfire }, opponentHealth: 4);

        var result = new BeamSearch().Search(
            state,
            new BeamSearchOptions(BeamWidth: 64, MaximumActions: 3, TopK: 3, TimeBudget: TimeSpan.FromSeconds(2)));

        var best = Assert.IsType<SearchRoute>(result.Routes.First());
        var action = Assert.IsType<PlayCardAction>(best.Actions.First());
        Assert.Equal((10, 200), (action.SourceEntityId, action.TargetEntityId));
        Assert.Equal(0, best.State.Opponent.Hero.Health);
    }

    [Fact]
    public void ExpandsRandomDiscardBranchesWithProbability()
    {
        var hand = new[]
        {
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.Soulfire, 10),
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.BonewebEgg, 11),
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.HandOfGuldan, 12)
        };

        var result = new BeamSearch().Search(
            CreateState(hand),
            new BeamSearchOptions(BeamWidth: 256, MaximumActions: 1, TopK: 256, TimeBudget: TimeSpan.FromSeconds(2)));
        var soulfireBranches = result.Routes.Where(route => route.Actions.FirstOrDefault() is PlayCardAction action &&
                                                            action.SourceEntityId == 10 && action.TargetEntityId == 200)
            .ToArray();

        Assert.Equal(2, soulfireBranches.Length);
        Assert.All(soulfireBranches, route => Assert.Equal(0.5d, route.Probability, 10));
    }

    [Fact]
    public void DeduplicatesCommutativeActionOrders()
    {
        var first = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.EntropicContinuity, 10);
        var second = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.EntropicContinuity, 11);

        var result = new BeamSearch().Search(
            CreateState(new[] { first, second }),
            new BeamSearchOptions(BeamWidth: 256, MaximumActions: 2, TopK: 20, TimeBudget: TimeSpan.FromSeconds(2)));

        Assert.True(result.Metrics.DeduplicatedNodes > 0);
        Assert.All(result.Routes, route => Assert.True(route.Actions.Length <= 2));
    }

    [Fact]
    public void DominancePrunerKeepsStrictlyBetterManaRoute()
    {
        var state = CreateState(Array.Empty<HandCardState>());
        var lowerMana = state.WithPlayer(PlayerSide.Friendly, state.Friendly with
        {
            Mana = state.Friendly.Mana with { Available = 2, Spent = 8 }
        });
        var higherMana = state.WithPlayer(PlayerSide.Friendly, state.Friendly with
        {
            Mana = state.Friendly.Mana with { Available = 4, Spent = 6 }
        });
        var routes = new[]
        {
            Route(lowerMana, 5),
            Route(higherMana, 5)
        };

        var kept = new DominancePruner().Prune(routes, out var pruned);

        Assert.Equal(1, pruned);
        Assert.Equal(4, Assert.Single(kept).State.Friendly.Mana.Available);
    }

    [Fact]
    public void StateKeyIsDeterministicAndSensitiveToVisibleState()
    {
        var state = CreateState(Array.Empty<HandCardState>());
        var damaged = state.WithPlayer(PlayerSide.Friendly, state.Friendly with
        {
            Hero = state.Friendly.Hero with { Health = 29 }
        });

        Assert.Equal(RuleStateKey.Calculate(state), RuleStateKey.Calculate(state));
        Assert.NotEqual(RuleStateKey.Calculate(state), RuleStateKey.Calculate(damaged));
    }

    [Fact]
    public void HonorsCancellationAndTimeBudget()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = new BeamSearch().Search(
            CreateState(Array.Empty<HandCardState>()),
            new BeamSearchOptions(TimeBudget: TimeSpan.FromSeconds(1)),
            cancellation.Token);
        var timedOut = new BeamSearch().Search(
            CreateState(Array.Empty<HandCardState>()),
            new BeamSearchOptions(TimeBudget: TimeSpan.FromTicks(1)));

        Assert.True(cancelled.Metrics.Cancelled);
        Assert.True(timedOut.Metrics.TimedOut);
    }

    private static SearchRoute Route(RuleGameState state, double score) => new(
        state,
        ImmutableArray<RuleAction>.Empty,
        ImmutableArray<RuleEvent>.Empty,
        1,
        score);

    private static RuleGameState CreateState(HandCardState[] hand, int opponentHealth = 30)
    {
        var friendly = PlayerState.Create(
            new HeroState(100, "HERO", 30, 30),
            new HeroPowerState(101, "POWER", 2),
            new ManaState(10, 0, 0, 10, 0, 0),
            hand);
        var opponent = PlayerState.Create(
            new HeroState(200, "OPPONENT_HERO", opponentHealth, 30),
            new HeroPowerState(201, "OPPONENT_POWER", 2),
            new ManaState(0, 0, 0, 0, 0, 0));
        return new RuleGameState(1, PlayerSide.Friendly, friendly, opponent);
    }
}
