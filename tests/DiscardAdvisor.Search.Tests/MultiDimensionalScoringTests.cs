using System;
using System.Collections.Immutable;
using System.Linq;
using DiscardAdvisor.Rules;
using DiscardAdvisor.Rules.Model;
using Xunit;

namespace DiscardAdvisor.Search.Tests;

public sealed class MultiDimensionalScoringTests
{
    [Fact]
    public void OpponentEvidenceSelectsEachOfTheThreeRiskProfiles()
    {
        var model = new OpponentBeliefModel();
        var aggro = model.Estimate(new OpponentEvidence(
            OpponentClass: "HUNTER",
            TurnNumber: 4,
            BoardAttack: 12,
            BoardMinions: 4,
            RevealedLowCostCards: 5,
            CardsPlayed: 7));
        var control = model.Estimate(new OpponentEvidence(
            OpponentClass: "PRIEST",
            TurnNumber: 8,
            RevealedRemovalCards: 5,
            OpponentHandSize: 9));
        var combo = model.Estimate(new OpponentEvidence(
            OpponentClass: "MAGE",
            TurnNumber: 8,
            BoardAttack: 1,
            RevealedComboCards: 5,
            OpponentHandSize: 8));

        Assert.Equal(OpponentArchetype.Aggro, aggro.MostLikely);
        Assert.Equal(OpponentArchetype.Control, control.MostLikely);
        Assert.Equal(OpponentArchetype.Combo, combo.MostLikely);
        Assert.All(new[] { aggro, control, combo }, belief =>
            Assert.Equal(1d, belief.Aggro + belief.Control + belief.Combo, 10));
    }

    [Fact]
    public void OpponentProfilesApplyDifferentStrategicWeights()
    {
        var survival = ScoreDimensions.Zero with { Survival = 1 };
        var directDamage = ScoreDimensions.Zero with { DirectDamage = 1 };

        Assert.True(
            ScoringWeights.For(new OpponentBelief(1, 0, 0)).WeightedSum(survival) >
            ScoringWeights.For(new OpponentBelief(0, 0, 1)).WeightedSum(survival));
        Assert.True(
            ScoringWeights.For(new OpponentBelief(0, 0, 1)).WeightedSum(directDamage) >
            ScoringWeights.For(new OpponentBelief(1, 0, 0)).WeightedSum(directDamage));
    }

    [Fact]
    public void EvaluatorKeepsDeckSpecificDimensionsIndependent()
    {
        var soulfire = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.Soulfire, 10);
        var temporary = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.HandOfGuldan, 11) with
        {
            Temporary = true
        };
        var duke = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.DukeOfBelow, 12, discardCount: 2);
        var shred = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.ShredOfTime, 40);
        var taunt = new MinionState(20, "TAUNT", 1, 2, 5, 5, Taunt: true);
        var state = CreateState(
            new[] { soulfire, temporary, duke },
            friendlyHealth: 10,
            board: new[] { taunt },
            deck: new[] { shred });

        var score = new MultiDimensionalStateEvaluator().EvaluateDetailed(state);

        Assert.Equal(4d, score.Dimensions.DirectDamage, 10);
        Assert.True(score.Dimensions.TemporaryValue < 0);
        Assert.True(score.Dimensions.SelfDamage < 0);
        Assert.True(score.Dimensions.DukeGrowth > 0);
        Assert.True(score.Dimensions.Board > 0);
        Assert.Equal(0, score.Dimensions.Lethal);
    }

    [Fact]
    public void RouteEventsAdjustDiscardTemporaryAndBoardSpaceDimensions()
    {
        var state = CreateState(Array.Empty<HandCardState>());
        var action = new EndTurnAction(PlayerSide.Friendly);
        var baseline = Route(state, action, ImmutableArray<RuleEvent>.Empty);
        var eventful = Route(state, action, ImmutableArray.Create(
            new RuleEvent("discard", null, 10, 1, DiscardWarlockCardIds.HandOfGuldan),
            new RuleEvent("discard_source", null, 10, 0, "temporary_expired"),
            new RuleEvent("summon_failed_board_full", 11, null, 0, "TOKEN")));
        var evaluator = new MultiDimensionalStateEvaluator();

        var baselineScore = evaluator.EvaluateRoute(state, baseline);
        var eventfulScore = evaluator.EvaluateRoute(state, eventful);

        Assert.True(eventfulScore.Dimensions.DiscardValue > baselineScore.Dimensions.DiscardValue);
        Assert.True(eventfulScore.Dimensions.TemporaryValue < baselineScore.Dimensions.TemporaryValue);
        Assert.True(eventfulScore.Dimensions.BoardSpace < baselineScore.Dimensions.BoardSpace);
    }

    [Fact]
    public void RiskStatisticsRetainExpectedP10VarianceLethalAndCoverage()
    {
        var complete = RouteRiskStatistics.Calculate(new[]
        {
            new RiskSample(100, 0.9, true),
            new RiskSample(0, 0.1)
        });
        var partial = RouteRiskStatistics.Calculate(new[]
        {
            new RiskSample(100, 0.5, true)
        });

        Assert.Equal(90, complete.Expected, 10);
        Assert.Equal(0, complete.P10, 10);
        Assert.Equal(900, complete.Variance, 10);
        Assert.Equal(0.9, complete.LethalProbability, 10);
        Assert.Equal(1, complete.CoverageProbability, 10);
        Assert.Equal(50, partial.Expected, 10);
        Assert.Equal(0, partial.P10, 10);
        Assert.Equal(0.5, partial.CoverageProbability, 10);
    }

    [Fact]
    public void RankerAggregatesOutcomesForTheSameActionSequence()
    {
        var initial = CreateState(Array.Empty<HandCardState>());
        var action = new EndTurnAction(PlayerSide.Friendly);
        var healthy = initial.WithPlayer(PlayerSide.Friendly, initial.Friendly with
        {
            Hero = initial.Friendly.Hero with { Health = 30 }
        });
        var damaged = initial.WithPlayer(PlayerSide.Friendly, initial.Friendly with
        {
            Hero = initial.Friendly.Hero with { Health = 5 }
        });
        var routes = new[]
        {
            Route(healthy, action, probability: 0.8),
            Route(damaged, action, probability: 0.2, usesMonteCarlo: true)
        };

        var candidate = Assert.Single(new RiskAwareRouteRanker().Rank(initial, routes));

        Assert.Equal(2, candidate.Outcomes.Length);
        Assert.Equal(1, candidate.Risk.CoverageProbability, 10);
        Assert.True(candidate.Risk.Variance > 0);
        Assert.Equal(0.9, candidate.Confidence, 10);
        Assert.Equal("route-1", candidate.CandidateId);
    }

    [Fact]
    public void RiskAdjustedRankingRejectsHigherMeanWithSevereDownside()
    {
        var initial = CreateState(Array.Empty<HandCardState>());
        var riskyAction = new UseHeroPowerAction(PlayerSide.Friendly);
        var safeAction = new EndTurnAction(PlayerSide.Friendly);
        var riskyHigh = initial.WithPlayer(PlayerSide.Friendly, initial.Friendly with
        {
            Hero = initial.Friendly.Hero with { Health = 110, MaxHealth = 110 }
        });
        var riskyLow = initial.WithPlayer(PlayerSide.Friendly, initial.Friendly with
        {
            Hero = initial.Friendly.Hero with { Health = 0 }
        });
        var safe = initial.WithPlayer(PlayerSide.Friendly, initial.Friendly with
        {
            Hero = initial.Friendly.Hero with { Health = 90, MaxHealth = 90 }
        });
        var routes = new[]
        {
            Route(riskyHigh, riskyAction, probability: 0.9),
            Route(riskyLow, riskyAction, probability: 0.1),
            Route(safe, safeAction)
        };

        var ranked = new RiskAwareRouteRanker(new HeroHealthEvaluator()).Rank(initial, routes);

        Assert.IsType<EndTurnAction>(ranked[0].Actions.Single());
        Assert.True(ranked[1].Risk.Expected > ranked[0].Risk.Expected);
        Assert.True(ranked[1].Risk.P10 < ranked[0].Risk.P10);
    }

    [Fact]
    public void BeamSearchReturnsRiskAwareLethalCandidate()
    {
        var barrage = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.SoulBarrage, 10);
        var state = CreateState(new[] { barrage }, opponentHealth: 5);

        var result = new BeamSearch().Search(
            state,
            new BeamSearchOptions(BeamWidth: 64, MaximumActions: 1, TopK: 5, TimeBudget: TimeSpan.FromSeconds(1)));

        var candidate = result.Candidates.First();
        var play = Assert.IsType<PlayCardAction>(candidate.Actions.First());
        Assert.Equal(barrage.EntityId, play.SourceEntityId);
        Assert.Equal(1, candidate.Risk.LethalProbability, 10);
        Assert.Equal(1, candidate.ExpectedDimensions.Lethal, 10);
    }

    private static SearchRoute Route(
        RuleGameState state,
        RuleAction action,
        ImmutableArray<RuleEvent>? events = null,
        double probability = 1,
        bool usesMonteCarlo = false) => new(
        state,
        ImmutableArray.Create(action),
        events ?? ImmutableArray<RuleEvent>.Empty,
        probability,
        0,
        usesMonteCarlo);

    private static RuleGameState CreateState(
        HandCardState[] hand,
        int friendlyHealth = 30,
        int opponentHealth = 30,
        MinionState[]? board = null,
        HandCardState[]? deck = null)
    {
        var friendly = PlayerState.Create(
            new HeroState(100, "HERO", friendlyHealth, 30),
            new HeroPowerState(101, "POWER", 2),
            new ManaState(10, 0, 0, 10, 0, 0),
            hand,
            board,
            deck: deck);
        var opponent = PlayerState.Create(
            new HeroState(200, "OPPONENT_HERO", opponentHealth, 30),
            new HeroPowerState(201, "OPPONENT_POWER", 2),
            new ManaState(0, 0, 0, 0, 0, 0));
        return new RuleGameState(1, PlayerSide.Friendly, friendly, opponent);
    }

    private sealed class HeroHealthEvaluator : IDetailedStateEvaluator
    {
        public double Evaluate(RuleGameState state) => state.Friendly.Hero.Health;

        public DetailedStateScore EvaluateDetailed(RuleGameState state, OpponentBelief? belief = null)
        {
            var value = state.Friendly.Hero.Health;
            return new DetailedStateScore(ScoreDimensions.Zero with { Survival = value }, value);
        }

        public DetailedStateScore EvaluateRoute(
            RuleGameState initialState,
            SearchRoute route,
            OpponentBelief? belief = null) => EvaluateDetailed(route.State, belief);
    }
}
