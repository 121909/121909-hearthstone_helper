using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using DiscardAdvisor.Rules.Model;
using DiscardAdvisor.Search;
using Xunit;

namespace DiscardAdvisor.Plugin.Tests;

public sealed class AdvisorOverlayPresenterTests
{
    [Fact]
    public void ReadyStateShowsPrimaryAlternativeRiskAndResolvedNames()
    {
        var initial = State(opponentHealth: 4);
        var lethal = initial.WithPlayer(PlayerSide.Opponent, initial.Opponent with
        {
            Hero = initial.Opponent.Hero with { Health = 0 }
        });
        var primaryRoute = Route(
            lethal,
            new PlayCardAction(PlayerSide.Friendly, 10, 200));
        var alternativeRoute = Route(
            initial,
            new EndTurnAction(PlayerSide.Friendly));
        var primary = Candidate("route-1", primaryRoute, lethalProbability: 1, confidence: 0.94);
        var alternative = Candidate("route-2", alternativeRoute, lethalProbability: 0, confidence: 0.72);
        var result = LocalResult(primaryRoute, new[] { primary, alternative }, deterministicLethal: true);
        var presenter = new AdvisorOverlayPresenter(new StubCardNames(new Dictionary<string, string>
        {
            ["EX1_308"] = "Soulfire",
            ["OPPONENT_HERO"] = "Opponent hero"
        }));

        var view = presenter.Present(PluginAdvisorUpdate.Ready("state-1", initial, result));

        Assert.Equal(PluginAdvisorStatus.Ready, view.Status);
        Assert.Equal("Lethal found", view.StatusText);
        Assert.NotNull(view.PrimaryRoute);
        Assert.Equal(OverlayRiskTone.Positive, view.PrimaryRoute!.RiskTone);
        Assert.Equal("Certain lethal", view.PrimaryRoute.Risk);
        Assert.Equal("Play Soulfire", Assert.Single(view.PrimaryRoute.Steps).Title);
        Assert.Contains("Opponent hero", view.PrimaryRoute.Steps[0].Detail, StringComparison.Ordinal);
        Assert.Single(view.Alternatives);
        Assert.Equal("Alternative 1", view.Alternatives[0].Title);
    }

    [Theory]
    [InlineData(PluginAdvisorStatus.Analyzing, "Analyzing", OverlayRiskTone.Neutral)]
    [InlineData(PluginAdvisorStatus.Stale, "State changed", OverlayRiskTone.Caution)]
    [InlineData(PluginAdvisorStatus.UnsupportedPatch, "Unsupported patch", OverlayRiskTone.Critical)]
    [InlineData(PluginAdvisorStatus.Offline, "Offline", OverlayRiskTone.Neutral)]
    public void PresenterMapsNonReadyStatuses(
        PluginAdvisorStatus status,
        string expectedText,
        OverlayRiskTone expectedTone)
    {
        var view = new AdvisorOverlayPresenter().Present(PluginAdvisorUpdate.StateOnly(status));

        Assert.False(view.HasRoutes);
        Assert.Equal(expectedText, view.StatusText);
        Assert.Equal(expectedTone, view.StatusTone);
    }

    [Fact]
    public void LowCoverageRouteUsesTextAndCriticalTone()
    {
        var state = State();
        var route = Route(state, new EndTurnAction(PlayerSide.Friendly), probability: 0.4);
        var candidate = Candidate("route-1", route, lethalProbability: 0, confidence: 0.5, coverage: 0.4);
        var result = LocalResult(route, new[] { candidate }, deterministicLethal: false);

        var view = new AdvisorOverlayPresenter().Present(PluginAdvisorUpdate.Ready("state-1", state, result));

        Assert.Equal(OverlayRiskTone.Critical, view.PrimaryRoute!.RiskTone);
        Assert.Contains("Coverage 40%", view.PrimaryRoute.Risk, StringComparison.Ordinal);
        Assert.Contains("Confidence 50%", view.PrimaryRoute.Confidence, StringComparison.Ordinal);
    }

    private static RuleGameState State(int opponentHealth = 30)
    {
        var hand = ImmutableArray.Create(new HandCardState(
            10,
            "EX1_308",
            1,
            RuleCardType.Spell,
            TargetKind: TargetKind.EnemyCharacter));
        var friendly = PlayerState.Create(
            new HeroState(100, "FRIENDLY_HERO", 30, 30),
            new HeroPowerState(101, "POWER", 2),
            new ManaState(10, 0, 0, 10, 0, 0),
            hand);
        var opponent = PlayerState.Create(
            new HeroState(200, "OPPONENT_HERO", opponentHealth, 30),
            new HeroPowerState(201, "OPPONENT_POWER", 2),
            new ManaState(0, 0, 0, 0, 0, 0));
        return new RuleGameState(5, PlayerSide.Friendly, friendly, opponent);
    }

    private static SearchRoute Route(
        RuleGameState state,
        RuleAction action,
        double probability = 1) => new(
        state,
        ImmutableArray.Create(action),
        ImmutableArray<RuleEvent>.Empty,
        probability,
        0);

    private static RiskAwareRouteCandidate Candidate(
        string id,
        SearchRoute route,
        double lethalProbability,
        double confidence,
        double coverage = 1)
    {
        var dimensions = ScoreDimensions.Zero with { Lethal = lethalProbability };
        return new RiskAwareRouteCandidate(
            id,
            route.Actions,
            ImmutableArray.Create(new ScoredRouteOutcome(route, new DetailedStateScore(dimensions, 0))),
            dimensions,
            new RouteRiskStatistics(0, 0, 0, lethalProbability, coverage),
            0,
            confidence,
            route);
    }

    private static LocalAdvisorResult LocalResult(
        SearchRoute route,
        IEnumerable<RiskAwareRouteCandidate> candidates,
        bool deterministicLethal) => new(
        ImmutableArray.Create(route),
        candidates.ToImmutableArray(),
        deterministicLethal,
        new LethalSearchResult(deterministicLethal, deterministicLethal ? route : null, 1, TimeSpan.Zero, false, false),
        null,
        TimeSpan.Zero);

    private sealed class StubCardNames : ICardNameResolver
    {
        private readonly IReadOnlyDictionary<string, string> _names;

        public StubCardNames(IReadOnlyDictionary<string, string> names)
        {
            _names = names;
        }

        public string Resolve(string cardId) => _names.TryGetValue(cardId, out var name) ? name : cardId;
    }
}
