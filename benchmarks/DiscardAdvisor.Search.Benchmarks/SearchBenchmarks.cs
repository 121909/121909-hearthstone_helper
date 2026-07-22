using System;
using System.Linq;
using BenchmarkDotNet.Attributes;
using DiscardAdvisor.Rules;
using DiscardAdvisor.Rules.Model;

namespace DiscardAdvisor.Search.Benchmarks;

[MemoryDiagnoser]
[WarmupCount(2)]
[IterationCount(5)]
public class SearchBenchmarks
{
    private BeamSearch _beamSearch = null!;
    private LocalTurnAdvisor _advisor = null!;
    private RuleGameState _state = null!;
    private BeamSearchOptions _beamOptions = null!;
    private LocalAdvisorOptions _advisorOptions = null!;

    [GlobalSetup]
    public void Setup()
    {
        var pool = new StaticRandomOneCostMinionPool(
            "benchmark-246003",
            Enumerable.Range(0, 128)
                .Select(index => new RandomOneCostMinion(
                    $"BENCH_ONE_{index:D3}",
                    1 + index % 3,
                    1 + index % 4,
                    Taunt: index % 11 == 0,
                    Rush: index % 17 == 0,
                    HasUnmodeledEffects: index % 5 == 0)));
        _beamSearch = new BeamSearch(pool);
        _advisor = new LocalTurnAdvisor(pool);
        _state = CreateDenseState();
        _beamOptions = new BeamSearchOptions(
            BeamWidth: 64,
            MaximumActions: 12,
            TopK: 5,
            TimeBudget: TimeSpan.FromMilliseconds(250),
            RandomSampling: new RandomSamplingOptions(Seed: 0x5EED, ExactOutcomeLimit: 64, MonteCarloSamples: 48));
        _advisorOptions = new LocalAdvisorOptions(_beamOptions, TimeSpan.FromMilliseconds(75));
    }

    [Benchmark(Baseline = true)]
    public BeamSearchResult DenseBeamSearch() => _beamSearch.Search(_state, _beamOptions);

    [Benchmark]
    public LocalAdvisorResult FullLocalAdvisor() => _advisor.Advise(_state, _advisorOptions);

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
        var friendlyBoard = new[]
        {
            new MinionState(20, "FRIENDLY_A", 1, 2, 3, 3),
            new MinionState(21, "FRIENDLY_B", 2, 3, 2, 2),
            new MinionState(22, "FRIENDLY_TAUNT", 3, 1, 5, 5, Taunt: true)
        };
        var opponentBoard = new[]
        {
            new MinionState(30, "OPPONENT_A", 1, 3, 2, 2),
            new MinionState(31, "OPPONENT_TAUNT", 2, 2, 4, 4, Taunt: true),
            new MinionState(32, "OPPONENT_B", 3, 4, 5, 5)
        };
        var friendly = PlayerState.Create(
            new HeroState(100, "FRIENDLY_HERO", 21, 30),
            new HeroPowerState(101, "WARLOCK_POWER", 2),
            new ManaState(10, 0, 0, 10, 0, 0),
            hand,
            friendlyBoard,
            deck: deck,
            discardCount: 2);
        var opponent = PlayerState.Create(
            new HeroState(200, "OPPONENT_HERO", 28, 30, Armor: 2),
            new HeroPowerState(201, "OPPONENT_POWER", 2),
            new ManaState(0, 0, 0, 10, 0, 0),
            board: opponentBoard,
            weapon: new WeaponState(40, "OPPONENT_WEAPON", 2, 2));
        return new RuleGameState(8, PlayerSide.Friendly, friendly, opponent, NextEntityId: 2000);
    }
}
