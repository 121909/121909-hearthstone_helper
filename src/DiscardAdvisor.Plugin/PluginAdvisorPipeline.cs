using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiscardAdvisor.Domain.Snapshots;
using DiscardAdvisor.Rules.Model;
using DiscardAdvisor.Search;

namespace DiscardAdvisor.Plugin;

public enum PluginAdvisorStatus
{
    Offline,
    Inactive,
    Analyzing,
    Ready,
    Stale,
    UnsupportedPatch,
    UnsupportedInteraction,
    NoLegalRoute
}

public sealed class PluginAdvisorUpdate
{
    private PluginAdvisorUpdate(
        PluginAdvisorStatus status,
        string? stateId,
        RuleGameState? state,
        LocalAdvisorResult? result,
        IReadOnlyList<string>? details)
    {
        Status = status;
        StateId = stateId;
        State = state;
        Result = result;
        Details = details ?? Array.Empty<string>();
    }

    public PluginAdvisorStatus Status { get; }
    public string? StateId { get; }
    public RuleGameState? State { get; }
    public LocalAdvisorResult? Result { get; }
    public IReadOnlyList<string> Details { get; }

    public static PluginAdvisorUpdate StateOnly(PluginAdvisorStatus status, string? stateId = null) =>
        new(status, stateId, null, null, null);

    public static PluginAdvisorUpdate Ready(
        string stateId,
        RuleGameState state,
        LocalAdvisorResult result) => new(
        result.Candidates.IsEmpty ? PluginAdvisorStatus.NoLegalRoute : PluginAdvisorStatus.Ready,
        stateId,
        state,
        result,
        null);

    public static PluginAdvisorUpdate Unsupported(
        string stateId,
        IEnumerable<string> details) => new(
        PluginAdvisorStatus.UnsupportedInteraction,
        stateId,
        null,
        null,
        (details ?? throw new ArgumentNullException(nameof(details))).ToArray());
}

public interface IOverlayStateSource
{
    PluginAdvisorUpdate CurrentAdvisorUpdate { get; }

    event Action<PluginAdvisorUpdate>? AdvisorUpdated;
}

public interface ILocalAdvisorService
{
    Task<PluginAdvisorUpdate> AnalyzeAsync(GameSnapshot snapshot, CancellationToken cancellationToken);
}

public sealed class LocalAdvisorService : ILocalAdvisorService
{
    private readonly SnapshotRuleStateMapper _mapper;
    private readonly LocalTurnAdvisor _advisor;
    private readonly LocalAdvisorOptions _options;

    public LocalAdvisorService(
        LocalTurnAdvisor advisor,
        SnapshotRuleStateMapper? mapper = null,
        LocalAdvisorOptions? options = null)
    {
        _advisor = advisor ?? throw new ArgumentNullException(nameof(advisor));
        _mapper = mapper ?? new SnapshotRuleStateMapper();
        _options = options ?? new LocalAdvisorOptions(new BeamSearchOptions());
    }

    public Task<PluginAdvisorUpdate> AnalyzeAsync(
        GameSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot is null)
            throw new ArgumentNullException(nameof(snapshot));
        return Task.Run(() => Analyze(snapshot, cancellationToken), cancellationToken);
    }

    private PluginAdvisorUpdate Analyze(GameSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!snapshot.IsStable ||
            !string.Equals(snapshot.ActivePlayer, "FRIENDLY", StringComparison.Ordinal) ||
            !string.Equals(snapshot.Step, "MAIN_ACTION", StringComparison.Ordinal))
        {
            return PluginAdvisorUpdate.StateOnly(PluginAdvisorStatus.Inactive, snapshot.StateId);
        }
        var mapping = _mapper.Map(snapshot);
        if (!mapping.IsSupported || mapping.State is null)
            return PluginAdvisorUpdate.Unsupported(snapshot.StateId, mapping.UnsupportedInteractions);
        var result = _advisor.Advise(mapping.State, _options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return PluginAdvisorUpdate.Ready(snapshot.StateId, mapping.State, result);
    }
}
