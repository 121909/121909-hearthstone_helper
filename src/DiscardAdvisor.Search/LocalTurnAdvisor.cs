using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;
using DiscardAdvisor.Rules.Model;

namespace DiscardAdvisor.Search;

public sealed record LocalAdvisorOptions(
    BeamSearchOptions BeamSearch,
    TimeSpan? LethalTimeBudget = null)
{
    public TimeSpan EffectiveLethalTimeBudget => LethalTimeBudget ?? TimeSpan.FromMilliseconds(75);
}
public sealed record LocalAdvisorResult(
    ImmutableArray<SearchRoute> Routes,
    ImmutableArray<RiskAwareRouteCandidate> Candidates,
    bool DeterministicLethalFound,
    LethalSearchResult LethalSearch,
    BeamSearchMetrics? BeamSearchMetrics,
    TimeSpan Elapsed);

public sealed class LocalTurnAdvisor
{
    private readonly DeterministicLethalSearch _lethalSearch;
    private readonly BeamSearch _beamSearch;
    private readonly RiskAwareRouteRanker _routeRanker;

    public LocalTurnAdvisor()
        : this(new DeterministicLethalSearch(), new BeamSearch())
    {
    }

    public LocalTurnAdvisor(IRandomOneCostMinionPool oneCostMinions)
        : this(new DeterministicLethalSearch(), new BeamSearch(oneCostMinions))
    {
    }

    public LocalTurnAdvisor(DeterministicLethalSearch lethalSearch, BeamSearch beamSearch)
    {
        _lethalSearch = lethalSearch ?? throw new ArgumentNullException(nameof(lethalSearch));
        _beamSearch = beamSearch ?? throw new ArgumentNullException(nameof(beamSearch));
        _routeRanker = new RiskAwareRouteRanker();
    }

    public LocalAdvisorResult Advise(
        RuleGameState state,
        LocalAdvisorOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new LocalAdvisorOptions(new BeamSearchOptions());
        var stopwatch = Stopwatch.StartNew();
        var lethalBudget = options.EffectiveLethalTimeBudget < options.BeamSearch.EffectiveTimeBudget
            ? options.EffectiveLethalTimeBudget
            : options.BeamSearch.EffectiveTimeBudget;
        var lethal = _lethalSearch.Search(
            state,
            options.BeamSearch.MaximumActions,
            lethalBudget,
            cancellationToken);
        if (lethal.Found && lethal.Route is not null)
        {
            var belief = options.BeamSearch.OpponentBelief ?? new OpponentBeliefModel().Estimate(state);
            var candidates = _routeRanker.Rank(state, new[] { lethal.Route }, belief, 1);
            stopwatch.Stop();
            return new LocalAdvisorResult(
                ImmutableArray.Create(lethal.Route),
                candidates,
                true,
                lethal,
                null,
                stopwatch.Elapsed);
        }

        var remaining = options.BeamSearch.EffectiveTimeBudget - stopwatch.Elapsed;
        if (remaining <= TimeSpan.Zero || cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new LocalAdvisorResult(
                ImmutableArray<SearchRoute>.Empty,
                ImmutableArray<RiskAwareRouteCandidate>.Empty,
                false,
                lethal,
                null,
                stopwatch.Elapsed);
        }
        var beamOptions = options.BeamSearch with { TimeBudget = remaining };
        var beam = _beamSearch.Search(state, beamOptions, cancellationToken);
        stopwatch.Stop();
        return new LocalAdvisorResult(
            beam.Routes,
            beam.Candidates,
            false,
            lethal,
            beam.Metrics,
            stopwatch.Elapsed);
    }
}
