using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DiscardAdvisor.Rules;
using DiscardAdvisor.Rules.Model;
using Xunit;

namespace DiscardAdvisor.Search.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SearchPerformanceCollection
{
    public const string Name = "Search performance";
}

[Collection(SearchPerformanceCollection.Name)]
public sealed class SearchPerformanceRegressionTests
{
    private const string ExpectedSeededSearchHash = "10c8b55b97d2ae0c98e707705b5a3eb868b9390a8b1cfa9f71a7fc0f4d64cde5";

    [Fact]
    [Trait("Category", "Performance")]
    public void DenseLocalAdvisorStaysWithinThreeHundredMillisecondEnvelope()
    {
        var pool = CreatePool(128);
        var advisor = new LocalTurnAdvisor(pool);
        var state = CreateDenseState();
        advisor.Advise(
            state,
            new LocalAdvisorOptions(new BeamSearchOptions(
                BeamWidth: 16,
                MaximumActions: 2,
                TopK: 2,
                TimeBudget: TimeSpan.FromMilliseconds(50))));
        var options = new LocalAdvisorOptions(
            new BeamSearchOptions(
                BeamWidth: 64,
                MaximumActions: 12,
                TopK: 5,
                TimeBudget: TimeSpan.FromMilliseconds(250),
                RandomSampling: new RandomSamplingOptions(
                    Seed: 0x5EED,
                    ExactOutcomeLimit: 64,
                    MonteCarloSamples: 48)),
            TimeSpan.FromMilliseconds(75));

        var stopwatch = Stopwatch.StartNew();
        var result = advisor.Advise(state, options);
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed <= TimeSpan.FromMilliseconds(300),
            $"Local={stopwatch.Elapsed.TotalMilliseconds:F1} ms, " +
            $"reported={result.Elapsed.TotalMilliseconds:F1} ms, " +
            $"lethal={result.LethalSearch.Elapsed.TotalMilliseconds:F1} ms, " +
            $"beam={result.BeamSearchMetrics?.Elapsed.TotalMilliseconds:F1} ms, " +
            $"expanded={result.BeamSearchMetrics?.ExpandedNodes}, " +
            $"generated={result.BeamSearchMetrics?.GeneratedNodes}.");
        Assert.True(result.Elapsed <= TimeSpan.FromMilliseconds(300));
        Assert.NotEmpty(result.Routes);
        Assert.NotEmpty(result.Candidates);
        Assert.All(result.Routes, route => Assert.InRange(route.Actions.Length, 1, 12));
    }

    [Fact]
    public void FixedSeedSearchMatchesGoldenHashAndRepeatsExactly()
    {
        var pool = CreatePool(64);
        var state = CreateSamplingState();
        var options = new BeamSearchOptions(
            BeamWidth: 64,
            MaximumActions: 1,
            TopK: 64,
            TimeBudget: TimeSpan.FromSeconds(2),
            RandomSampling: new RandomSamplingOptions(
                Seed: 20260722,
                ExactOutcomeLimit: 1,
                MonteCarloSamples: 24));

        var first = new BeamSearch(pool).Search(state, options);
        var second = new BeamSearch(pool).Search(state, options);
        var firstSignature = Signature(first);
        var secondSignature = Signature(second);
        var actualHash = Sha256(firstSignature);

        Assert.Equal(firstSignature, secondSignature);
        Assert.True(
            string.Equals(ExpectedSeededSearchHash, actualHash, StringComparison.Ordinal),
            $"Expected fixed-seed hash {ExpectedSeededSearchHash}, actual {actualHash}.");
    }

    [Fact]
    public void DifferentSeedsExploreDifferentSummonSamples()
    {
        var pool = CreatePool(64);
        var state = CreateSamplingState();
        var baseOptions = new BeamSearchOptions(
            BeamWidth: 64,
            MaximumActions: 1,
            TopK: 64,
            TimeBudget: TimeSpan.FromSeconds(2),
            RandomSampling: new RandomSamplingOptions(
                Seed: 1,
                ExactOutcomeLimit: 1,
                MonteCarloSamples: 12));
        var otherOptions = baseOptions with
        {
            RandomSampling = baseOptions.RandomSampling! with { Seed = 2 }
        };

        var first = new BeamSearch(pool).Search(state, baseOptions);
        var second = new BeamSearch(pool).Search(state, otherOptions);

        Assert.NotEqual(Signature(first), Signature(second));
    }

    private static StaticRandomOneCostMinionPool CreatePool(int count) => new(
        "regression-246003",
        Enumerable.Range(0, count).Select(index => new RandomOneCostMinion(
            $"REG_ONE_{index:D3}",
            1 + index % 3,
            1 + index % 4,
            Taunt: index % 11 == 0,
            Rush: index % 17 == 0,
            HasUnmodeledEffects: index % 5 == 0)));

    private static RuleGameState CreateSamplingState()
    {
        var acolytes = DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.DisposableAcolytes, 10);
        var friendly = PlayerState.Create(
            new HeroState(100, "FRIENDLY_HERO", 30, 30),
            new HeroPowerState(101, "WARLOCK_POWER", 2),
            new ManaState(10, 0, 0, 10, 0, 0),
            new[] { acolytes });
        var opponent = PlayerState.Create(
            new HeroState(200, "OPPONENT_HERO", 30, 30),
            new HeroPowerState(201, "OPPONENT_POWER", 2),
            new ManaState(0, 0, 0, 10, 0, 0));
        return new RuleGameState(6, PlayerSide.Friendly, friendly, opponent, NextEntityId: 1000);
    }

    private static RuleGameState CreateDenseState()
    {
        var hand = new[]
        {
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.Soulfire, 10),
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.SoulBarrage, 11),
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.DisposableAcolytes, 12),
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.WickedWhispers, 13),
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.HandOfGuldan, 14),
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.BonewebEgg, 15),
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.DukeOfBelow, 16, discardCount: 2),
            DiscardWarlockCardCatalog.Create(DiscardWarlockCardIds.EntropicContinuity, 17)
        };
        var deck = Enumerable.Range(0, 12)
            .Select(index => DiscardWarlockCardCatalog.Create(
                index % 3 == 0 ? DiscardWarlockCardIds.ShredOfTime : DiscardWarlockCardIds.WalkingDead,
                1000 + index))
            .ToArray();
        var friendly = PlayerState.Create(
            new HeroState(100, "FRIENDLY_HERO", 21, 30),
            new HeroPowerState(101, "WARLOCK_POWER", 2),
            new ManaState(10, 0, 0, 10, 0, 0),
            hand,
            new[]
            {
                new MinionState(20, "FRIENDLY_A", 1, 2, 3, 3),
                new MinionState(21, "FRIENDLY_B", 2, 3, 2, 2),
                new MinionState(22, "FRIENDLY_TAUNT", 3, 1, 5, 5, Taunt: true)
            },
            deck: deck,
            discardCount: 2);
        var opponent = PlayerState.Create(
            new HeroState(200, "OPPONENT_HERO", 28, 30, Armor: 2),
            new HeroPowerState(201, "OPPONENT_POWER", 2),
            new ManaState(0, 0, 0, 10, 0, 0),
            board: new[]
            {
                new MinionState(30, "OPPONENT_A", 1, 3, 2, 2),
                new MinionState(31, "OPPONENT_TAUNT", 2, 2, 4, 4, Taunt: true),
                new MinionState(32, "OPPONENT_B", 3, 4, 5, 5)
            },
            weapon: new WeaponState(40, "OPPONENT_WEAPON", 2, 2));
        return new RuleGameState(8, PlayerSide.Friendly, friendly, opponent, NextEntityId: 2000);
    }

    private static string Signature(BeamSearchResult result)
    {
        var lines = result.Candidates.SelectMany(candidate => candidate.Outcomes
            .OrderBy(outcome => RuleStateKey.Calculate(outcome.Route.State), StringComparer.Ordinal)
            .Select(outcome => string.Join(
                "|",
                candidate.CandidateId,
                RiskAwareRouteRanker.ActionSequenceKey(outcome.Route),
                candidate.Risk.Expected.ToString("R", CultureInfo.InvariantCulture),
                candidate.Risk.P10.ToString("R", CultureInfo.InvariantCulture),
                candidate.Risk.Variance.ToString("R", CultureInfo.InvariantCulture),
                candidate.Risk.LethalProbability.ToString("R", CultureInfo.InvariantCulture),
                candidate.Risk.CoverageProbability.ToString("R", CultureInfo.InvariantCulture),
                outcome.Route.Probability.ToString("R", CultureInfo.InvariantCulture),
                RuleStateKey.Calculate(outcome.Route.State),
                string.Join(",", outcome.Route.Events.Select(ruleEvent =>
                    $"{ruleEvent.Type}:{ruleEvent.SourceEntityId}:{ruleEvent.TargetEntityId}:{ruleEvent.Amount}:{ruleEvent.CardId}")))));
        return string.Join("\n", lines);
    }

    private static string Sha256(string value) => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)))
        .ToLowerInvariant();
}
