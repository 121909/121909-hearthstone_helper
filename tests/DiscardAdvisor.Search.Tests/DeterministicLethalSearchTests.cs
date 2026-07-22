using System;
using System.Linq;
using DiscardAdvisor.Rules;
using DiscardAdvisor.Rules.Model;
using Xunit;

namespace DiscardAdvisor.Search.Tests;

public sealed class DeterministicLethalSearchTests
{
    [Fact]
    public void FindsSoulfireLethalEvenWhenDiscardOutcomeIsRandom()
    {
        var hand = new[]
        {
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.Soulfire, 10),
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.BonewebEgg, 11),
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.HandOfGuldan, 12)
        };

        var result = new DeterministicLethalSearch().Search(
            CreateState(hand, opponentHealth: 4),
            4,
            TimeSpan.FromSeconds(1));

        Assert.True(result.Found);
        var action = Assert.IsType<PlayCardAction>(result.Route!.Actions.Last());
        Assert.Equal(200, action.TargetEntityId);
    }

    [Fact]
    public void DoesNotTreatPendingRandomBarrageAsDeterministicLethal()
    {
        var barrage = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.SoulBarrage, 10);

        var result = new DeterministicLethalSearch().Search(
            CreateState(new[] { barrage }, opponentHealth: 5),
            2,
            TimeSpan.FromSeconds(1));

        Assert.False(result.Found);
    }

    [Fact]
    public void FindsOrderedAttacksThroughTaunt()
    {
        var first = new MinionState(20, "FIRST", 1, 2, 2, 2);
        var second = new MinionState(21, "SECOND", 2, 3, 3, 3);
        var taunt = new MinionState(30, "TAUNT", 1, 2, 2, 2, Taunt: true);
        var state = CreateState(
            Array.Empty<HandCardState>(),
            opponentHealth: 3,
            board: new[] { first, second },
            opponentBoard: new[] { taunt });

        var result = new DeterministicLethalSearch().Search(state, 3, TimeSpan.FromSeconds(1));

        Assert.True(result.Found);
        Assert.Equal(2, result.Route!.Actions.Length);
        Assert.Equal(30, Assert.IsType<AttackAction>(result.Route.Actions[0]).TargetEntityId);
        Assert.Equal(200, Assert.IsType<AttackAction>(result.Route.Actions[1]).TargetEntityId);
    }

    [Fact]
    public void LocalAdvisorReturnsProvenLethalBeforeBeamCandidates()
    {
        var soulfire = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.Soulfire, 10);
        var options = new LocalAdvisorOptions(
            new BeamSearchOptions(BeamWidth: 64, MaximumActions: 4, TopK: 5, TimeBudget: TimeSpan.FromSeconds(1)),
            TimeSpan.FromMilliseconds(200));

        var result = new LocalTurnAdvisor().Advise(CreateState(new[] { soulfire }, opponentHealth: 4), options);

        Assert.True(result.DeterministicLethalFound);
        Assert.Null(result.BeamSearchMetrics);
        Assert.Single(result.Routes);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(1, candidate.Risk.LethalProbability, 10);
    }

    [Fact]
    public void LocalAdvisorFallsBackToBeamWhenNoLethalExists()
    {
        var minion = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.WalkingDead, 10);

        var result = new LocalTurnAdvisor().Advise(
            CreateState(new[] { minion }),
            new LocalAdvisorOptions(new BeamSearchOptions(TimeBudget: TimeSpan.FromSeconds(1))));

        Assert.False(result.DeterministicLethalFound);
        Assert.NotNull(result.BeamSearchMetrics);
        Assert.NotEmpty(result.Routes);
        Assert.NotEmpty(result.Candidates);
    }

    private static RuleGameState CreateState(
        HandCardState[] hand,
        int opponentHealth = 30,
        MinionState[]? board = null,
        MinionState[]? opponentBoard = null)
    {
        var friendly = PlayerState.Create(
            new HeroState(100, "HERO", 30, 30),
            new HeroPowerState(101, "POWER", 2),
            new ManaState(10, 0, 0, 10, 0, 0),
            hand,
            board);
        var opponent = PlayerState.Create(
            new HeroState(200, "OPPONENT_HERO", opponentHealth, 30),
            new HeroPowerState(201, "OPPONENT_POWER", 2),
            new ManaState(0, 0, 0, 0, 0, 0),
            board: opponentBoard);
        return new RuleGameState(1, PlayerSide.Friendly, friendly, opponent);
    }
}
