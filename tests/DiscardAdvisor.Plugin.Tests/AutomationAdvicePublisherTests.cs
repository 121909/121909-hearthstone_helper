using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using DiscardAdvisor.Rules;
using DiscardAdvisor.Rules.Model;
using DiscardAdvisor.Search;
using Json.Schema;
using Xunit;

namespace DiscardAdvisor.Plugin.Tests;

public sealed class AutomationAdvicePublisherTests
{
    [Fact]
    public void PublishesReadyAdviceWithExecutableFirstStepAndEntityLocations()
    {
        var directory = TemporaryDirectory();
        try
        {
            var state = State();
            var action = new PlayCardAction(PlayerSide.Friendly, 10, 200);
            var route = Route(state, action);
            var update = PluginAdvisorUpdate.Ready("turn-1:ready", state, Result(route, confidence: 0.9));
            var sink = new FileAutomationAdviceSink(
                directory,
                "0.4.14",
                "0.3.4",
                () => DateTimeOffset.Parse("2026-07-24T12:00:00Z"));

            sink.Publish(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), update);

            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "current-advice.json")));
            var root = document.RootElement;
            var buildOptions = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
            JsonSchema.FromText(File.ReadAllText(Path.Combine("schemas", "common.schema.json")), buildOptions);
            var schema = JsonSchema.FromText(
                File.ReadAllText(Path.Combine("schemas", "automation-advice.schema.json")),
                buildOptions);
            var schemaResult = schema.Evaluate(root, new EvaluationOptions { OutputFormat = OutputFormat.List });
            Assert.True(schemaResult.IsValid);
            Assert.Equal("READY", root.GetProperty("status").GetString());
            Assert.True(root.GetProperty("automationAllowed").GetBoolean());
            Assert.Equal("0.4.14", root.GetProperty("pluginVersion").GetString());
            Assert.Equal("0.3.4", root.GetProperty("ruleSetVersion").GetString());
            var step = root.GetProperty("steps")[0];
            Assert.Equal("PLAY_CARD", step.GetProperty("type").GetString());
            Assert.Equal("FRIENDLY_HAND", step.GetProperty("source").GetProperty("zone").GetString());
            Assert.Equal(0, step.GetProperty("source").GetProperty("index").GetInt32());
            Assert.Equal("OPPONENT_HERO", step.GetProperty("target").GetProperty("zone").GetString());
            Assert.Single(File.ReadAllLines(Path.Combine(directory, "advice-history.jsonl")));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void BlocksTimedOutAndUnmodeledRandomRoutes()
    {
        var directory = TemporaryDirectory();
        try
        {
            var state = State();
            var action = new EndTurnAction(PlayerSide.Friendly);
            var route = Route(state, action, new RuleEvent("random_summon_effect_unmodeled"));
            var lethal = new LethalSearchResult(false, null, 1, TimeSpan.FromMilliseconds(75), true, false);
            var beam = new BeamSearchMetrics(1, 1, 0, 0, TimeSpan.FromMilliseconds(175), true, false);
            var result = Result(route, confidence: 0.8, lethal: lethal, beam: beam);
            var sink = new FileAutomationAdviceSink(directory, "0.4.14", "0.3.4");

            sink.Publish(Guid.Empty, PluginAdvisorUpdate.Ready("turn-1:blocked", state, result));

            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "current-advice.json")));
            var root = document.RootElement;
            Assert.False(root.GetProperty("automationAllowed").GetBoolean());
            var blockers = root.GetProperty("blockers").EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert.Contains("search_timed_out", blockers);
            Assert.Contains("route_event:random_summon_effect_unmodeled", blockers);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void StateChangesReplaceCurrentAdviceWithNonExecutableStatus()
    {
        var directory = TemporaryDirectory();
        try
        {
            var state = State();
            var route = Route(state, new EndTurnAction(PlayerSide.Friendly));
            var sink = new FileAutomationAdviceSink(directory, "0.4.14", "0.3.4");
            sink.Publish(Guid.Empty, PluginAdvisorUpdate.Ready("turn-1:ready", state, Result(route, 0.9)));

            sink.Publish(Guid.Empty, PluginAdvisorUpdate.StateOnly(PluginAdvisorStatus.Stale));

            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "current-advice.json")));
            Assert.Equal("STALE", document.RootElement.GetProperty("status").GetString());
            Assert.False(document.RootElement.GetProperty("automationAllowed").GetBoolean());
            Assert.Empty(document.RootElement.GetProperty("steps").EnumerateArray());
            Assert.Equal(2, File.ReadAllLines(Path.Combine(directory, "advice-history.jsonl")).Length);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void BlocksAdviceWhenTheFirstStepTargetCannotBeLocated()
    {
        var directory = TemporaryDirectory();
        try
        {
            var state = State();
            var route = Route(state, new PlayCardAction(PlayerSide.Friendly, 10, 999));
            var sink = new FileAutomationAdviceSink(directory, "0.4.14", "0.3.4");

            sink.Publish(Guid.Empty, PluginAdvisorUpdate.Ready("turn-1:unknown-target", state, Result(route, 0.9)));

            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "current-advice.json")));
            var root = document.RootElement;
            Assert.False(root.GetProperty("automationAllowed").GetBoolean());
            Assert.Contains(
                root.GetProperty("blockers").EnumerateArray().Select(item => item.GetString()),
                item => item == "target_location_unknown");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(DiscardWarlockCardIds.OcularOccultist, "HAND_DISCARD", "FRIENDLY_HAND")]
    [InlineData(DiscardWarlockCardIds.ChamberOfViscidus, "HAND_DISCARD", "CHOICE")]
    [InlineData(DiscardWarlockCardIds.CursedCatacombs, "DISCOVER", "CHOICE")]
    public void LocatesChoiceTargetsAccordingToTheClientInteraction(
        string sourceCardId,
        string choiceType,
        string expectedZone)
    {
        var directory = TemporaryDirectory();
        try
        {
            var state = State() with
            {
                PendingChoice = new PendingChoiceState(
                    700,
                    choiceType,
                    sourceCardId,
                    ImmutableArray.Create(new ChoiceCandidateState(10, "EX1_308")),
                    300)
            };
            var action = new SelectChoiceAction(PlayerSide.Friendly, 700, 10);
            var sink = new FileAutomationAdviceSink(directory, "0.4.14", "0.3.4");

            sink.Publish(Guid.Empty, PluginAdvisorUpdate.Ready("turn-1:choice", state, Result(Route(state, action), 0.9)));

            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "current-advice.json")));
            var step = document.RootElement.GetProperty("steps")[0];
            Assert.Equal("SELECT_CHOICE", step.GetProperty("type").GetString());
            Assert.Equal(expectedZone, step.GetProperty("target").GetProperty("zone").GetString());
            Assert.True(document.RootElement.GetProperty("automationAllowed").GetBoolean());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static RuleGameState State()
    {
        var friendly = PlayerState.Create(
            new HeroState(100, "FRIENDLY_HERO", 30, 30),
            new HeroPowerState(101, "CS2_056", 2),
            new ManaState(10, 0, 0, 10, 0, 0),
            new[] { new HandCardState(10, "EX1_308", 1, RuleCardType.Spell, TargetKind: TargetKind.EnemyCharacter) });
        var opponent = PlayerState.Create(
            new HeroState(200, "OPPONENT_HERO", 30, 30),
            new HeroPowerState(201, "OPPONENT_POWER", 2),
            new ManaState(0, 0, 0, 0, 0, 0));
        return new RuleGameState(1, PlayerSide.Friendly, friendly, opponent);
    }

    private static SearchRoute Route(RuleGameState state, RuleAction action, params RuleEvent[] events) => new(
        state,
        ImmutableArray.Create(action),
        events.ToImmutableArray(),
        1,
        0);

    private static LocalAdvisorResult Result(
        SearchRoute route,
        double confidence,
        LethalSearchResult? lethal = null,
        BeamSearchMetrics? beam = null)
    {
        var dimensions = ScoreDimensions.Zero;
        var candidate = new RiskAwareRouteCandidate(
            "route-1",
            route.Actions,
            ImmutableArray.Create(new ScoredRouteOutcome(route, new DetailedStateScore(dimensions, 0))),
            dimensions,
            new RouteRiskStatistics(0, 0, 0, 0, 1),
            0,
            confidence,
            route);
        return new LocalAdvisorResult(
            ImmutableArray.Create(route),
            ImmutableArray.Create(candidate),
            false,
            lethal ?? new LethalSearchResult(false, null, 1, TimeSpan.Zero, false, false),
            beam,
            TimeSpan.Zero);
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "discard-advisor-automation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
