using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DiscardAdvisor.Domain;
using DiscardAdvisor.Rules;
using DiscardAdvisor.Rules.Model;
using DiscardAdvisor.Search;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace DiscardAdvisor.Plugin;

public interface IAutomationAdviceSink
{
    void Publish(Guid gameId, PluginAdvisorUpdate update);
}

public sealed class NullAutomationAdviceSink : IAutomationAdviceSink
{
    public static NullAutomationAdviceSink Instance { get; } = new();

    private NullAutomationAdviceSink()
    {
    }

    public void Publish(Guid gameId, PluginAdvisorUpdate update)
    {
    }
}

public sealed class FileAutomationAdviceSink : IAutomationAdviceSink
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Ignore,
        Formatting = Formatting.Indented
    };

    private static readonly JsonSerializerSettings JsonLineSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Ignore,
        Formatting = Formatting.None
    };

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _currentPath;
    private readonly string _historyPath;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly string _pluginVersion;
    private readonly string _ruleSetVersion;

    public FileAutomationAdviceSink(
        string directory,
        string pluginVersion,
        string ruleSetVersion,
        Func<DateTimeOffset>? utcNow = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("An automation advice directory is required.", nameof(directory));
        if (string.IsNullOrWhiteSpace(pluginVersion))
            throw new ArgumentException("A plugin version is required.", nameof(pluginVersion));
        if (string.IsNullOrWhiteSpace(ruleSetVersion))
            throw new ArgumentException("A rule-set version is required.", nameof(ruleSetVersion));

        _directory = directory;
        _currentPath = Path.Combine(directory, "current-advice.json");
        _historyPath = Path.Combine(directory, "advice-history.jsonl");
        _pluginVersion = pluginVersion;
        _ruleSetVersion = ruleSetVersion;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public void Publish(Guid gameId, PluginAdvisorUpdate update)
    {
        if (update is null)
            throw new ArgumentNullException(nameof(update));

        var document = CreateDocument(gameId, update);
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                var temporaryPath = _currentPath + ".tmp";
                File.WriteAllText(
                    temporaryPath,
                    JsonConvert.SerializeObject(document, JsonSettings) + Environment.NewLine,
                    new UTF8Encoding(false));
                if (File.Exists(_currentPath))
                    File.Replace(temporaryPath, _currentPath, null);
                else
                    File.Move(temporaryPath, _currentPath);
                File.AppendAllText(
                    _historyPath,
                    JsonConvert.SerializeObject(document, JsonLineSettings) + Environment.NewLine,
                    new UTF8Encoding(false));
            }
        }
        catch (IOException)
        {
            // Advice export must never interrupt HDT. The Windows runner treats a stale file as unavailable.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private AutomationAdviceDocument CreateDocument(Guid gameId, PluginAdvisorUpdate update)
    {
        var status = ToProtocolStatus(update.Status);
        if (update.Status != PluginAdvisorStatus.Ready || update.State is null || update.Result is null ||
            update.Result.Candidates.IsEmpty)
        {
            return new AutomationAdviceDocument(
                "1.0.0",
                _pluginVersion,
                _ruleSetVersion,
                _utcNow(),
                gameId.ToString("N"),
                update.StateId,
                status,
                null,
                0,
                0,
                0,
                false,
                StatusBlockers(update),
                null,
                Array.Empty<AutomationAdviceStep>());
        }

        var candidate = update.Result.Candidates[0];
        var steps = candidate.Actions.Select((action, index) => MapStep(
                update.State,
                candidate.RepresentativeRoute.State,
                action,
                index))
            .ToArray();
        var blockers = FindAutomationBlockers(update.Result, candidate)
            .Concat(FindStepBlockers(steps.FirstOrDefault()))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new AutomationAdviceDocument(
            "1.0.0",
            _pluginVersion,
            _ruleSetVersion,
            _utcNow(),
            gameId.ToString("N"),
            update.StateId,
            status,
            candidate.CandidateId,
            candidate.Confidence,
            candidate.Risk.CoverageProbability,
            candidate.Risk.LethalProbability,
            blockers.Length == 0 && steps.Length > 0,
            blockers,
            new AutomationStateLayout(
                update.State.Friendly.Hand.Length,
                update.State.Friendly.BoardCount,
                update.State.Opponent.BoardCount,
                update.State.PendingChoice?.Candidates.Length ?? 0),
            steps);
    }

    private static IReadOnlyList<string> StatusBlockers(PluginAdvisorUpdate update)
    {
        if (update.Status == PluginAdvisorStatus.UnsupportedInteraction)
            return update.Details.Count == 0 ? new[] { "unsupported_interaction" } : update.Details;
        return new[] { "status:" + ToProtocolStatus(update.Status).ToLowerInvariant() };
    }

    private static IEnumerable<string> FindAutomationBlockers(
        LocalAdvisorResult result,
        RiskAwareRouteCandidate candidate)
    {
        if (result.LethalSearch.Cancelled || result.BeamSearchMetrics?.Cancelled == true)
            yield return "search_cancelled";
        if (result.LethalSearch.TimedOut || result.BeamSearchMetrics?.TimedOut == true)
            yield return "search_timed_out";
        if (candidate.Confidence < 0.65d)
            yield return "low_confidence:" + candidate.Confidence.ToString("0.000", CultureInfo.InvariantCulture);
        if (candidate.Risk.CoverageProbability < 0.8d)
            yield return "low_coverage:" + candidate.Risk.CoverageProbability.ToString("0.000", CultureInfo.InvariantCulture);

        foreach (var eventType in candidate.Outcomes
                     .SelectMany(outcome => outcome.Route.Events)
                     .Select(ruleEvent => ruleEvent.Type)
                     .Where(type => type == "random_summon_effect_unmodeled" ||
                                    type.EndsWith("_unresolved", StringComparison.Ordinal) ||
                                    type.EndsWith("_pending", StringComparison.Ordinal))
                     .Distinct(StringComparer.Ordinal))
        {
            yield return "route_event:" + eventType;
        }
    }

    private static IEnumerable<string> FindStepBlockers(AutomationAdviceStep? step)
    {
        if (step is null)
        {
            yield return "route_empty";
            yield break;
        }
        if (string.Equals(step.Type, "UNSUPPORTED", StringComparison.Ordinal))
            yield return "unsupported_action";
        if (step.Type is "PLAY_CARD" or "ATTACK" or "USE_HERO_POWER" or "USE_LOCATION")
        {
            if (step.Source is null || string.Equals(step.Source.Zone, "UNKNOWN", StringComparison.Ordinal))
                yield return "source_location_unknown";
        }
        if (step.TargetEntityId.HasValue &&
            (step.Target is null || string.Equals(step.Target.Zone, "UNKNOWN", StringComparison.Ordinal)))
        {
            yield return "target_location_unknown";
        }
    }

    private static AutomationAdviceStep MapStep(
        RuleGameState initialState,
        RuleGameState finalState,
        RuleAction action,
        int index)
    {
        var sourceId = SourceEntityId(initialState, action);
        var targetId = TargetEntityId(action);
        var target = action is SelectChoiceAction
            ? LocateChoice(initialState, targetId)
            : LocateEntity(initialState, finalState, targetId);
        return new AutomationAdviceStep(
            index,
            ActionType(action),
            sourceId,
            targetId,
            action is PlayCardAction play ? play.BoardPosition : null,
            action is SelectChoiceAction choice ? choice.ChoiceId : null,
            LocateEntity(initialState, finalState, sourceId),
            target);
    }

    private static AutomationEntityLocator? LocateChoice(RuleGameState state, int? entityId)
    {
        if (entityId is not int id || state.PendingChoice is null)
            return LocateEntity(state, state, entityId);
        if (state.PendingChoice.SourceCardId == DiscardWarlockCardIds.OcularOccultist)
            return LocateEntity(state, state, entityId);
        var candidateIndex = IndexOfEntity(state.PendingChoice.Candidates.Select(candidate => candidate.EntityId), id);
        if (candidateIndex < 0)
            return LocateEntity(state, state, entityId);
        var candidate = state.PendingChoice.Candidates[candidateIndex];
        return new AutomationEntityLocator(
            id,
            candidate.CardId,
            "CHOICE",
            candidateIndex,
            state.PendingChoice.Candidates.Length);
    }

    private static AutomationEntityLocator? LocateEntity(
        RuleGameState initialState,
        RuleGameState finalState,
        int? entityId)
    {
        if (entityId is not int id)
            return null;
        return LocateEntity(initialState, id) ?? LocateEntity(finalState, id);
    }

    private static AutomationEntityLocator? LocateEntity(RuleGameState state, int entityId)
    {
        var friendly = LocatePlayer(state.Friendly, entityId, "FRIENDLY");
        if (friendly is not null)
            return friendly;
        var opponent = LocatePlayer(state.Opponent, entityId, "OPPONENT");
        if (opponent is not null)
            return opponent;
        if (state.PendingChoice is not null)
        {
            var candidateIndex = IndexOfEntity(state.PendingChoice.Candidates.Select(candidate => candidate.EntityId), entityId);
            if (candidateIndex >= 0)
            {
                var candidate = state.PendingChoice.Candidates[candidateIndex];
                return new AutomationEntityLocator(
                    entityId,
                    candidate.CardId,
                    "CHOICE",
                    candidateIndex,
                    state.PendingChoice.Candidates.Length);
            }
        }
        return new AutomationEntityLocator(entityId, null, "UNKNOWN", null, null);
    }

    private static AutomationEntityLocator? LocatePlayer(PlayerState player, int entityId, string side)
    {
        if (player.Hero.EntityId == entityId)
            return new AutomationEntityLocator(entityId, player.Hero.CardId, side + "_HERO", 0, 1);
        if (player.HeroPower.EntityId == entityId)
            return new AutomationEntityLocator(entityId, player.HeroPower.CardId, side + "_HERO_POWER", 0, 1);
        if (player.Weapon?.EntityId == entityId)
            return new AutomationEntityLocator(entityId, player.Weapon.CardId, side + "_WEAPON", 0, 1);

        var handIndex = IndexOfEntity(player.Hand.Select(card => card.EntityId), entityId);
        if (handIndex >= 0)
            return new AutomationEntityLocator(entityId, player.Hand[handIndex].CardId, side + "_HAND", handIndex, player.Hand.Length);
        var minion = player.Board.FirstOrDefault(card => card.EntityId == entityId);
        if (minion is not null)
            return new AutomationEntityLocator(entityId, minion.CardId, side + "_BOARD", minion.BoardPosition - 1, player.BoardCount);
        var location = player.Locations.FirstOrDefault(card => card.EntityId == entityId);
        if (location is not null)
            return new AutomationEntityLocator(entityId, location.CardId, side + "_BOARD", location.BoardPosition - 1, player.BoardCount);
        return null;
    }

    private static int IndexOfEntity(IEnumerable<int> entityIds, int expected)
    {
        var index = 0;
        foreach (var entityId in entityIds)
        {
            if (entityId == expected)
                return index;
            index++;
        }
        return -1;
    }

    private static int? SourceEntityId(RuleGameState state, RuleAction action) => action switch
    {
        PlayCardAction play => play.SourceEntityId,
        AttackAction attack => attack.SourceEntityId,
        UseLocationAction location => location.SourceEntityId,
        UseHeroPowerAction power => state.Player(power.Side).HeroPower.EntityId,
        _ => null
    };

    private static int? TargetEntityId(RuleAction action) => action switch
    {
        PlayCardAction play => play.TargetEntityId,
        AttackAction attack => attack.TargetEntityId,
        UseHeroPowerAction power => power.TargetEntityId,
        UseLocationAction location => location.SelectedEntityId,
        SelectChoiceAction choice => choice.SelectedEntityId,
        _ => null
    };

    private static string ActionType(RuleAction action) => action switch
    {
        PlayCardAction => "PLAY_CARD",
        AttackAction => "ATTACK",
        UseHeroPowerAction => "USE_HERO_POWER",
        UseLocationAction => "USE_LOCATION",
        SelectChoiceAction => "SELECT_CHOICE",
        EndTurnAction => "END_TURN",
        _ => "UNSUPPORTED"
    };

    private static string ToProtocolStatus(PluginAdvisorStatus status) => status switch
    {
        PluginAdvisorStatus.NoLegalRoute => "NO_LEGAL_ROUTE",
        PluginAdvisorStatus.UnsupportedPatch => "UNSUPPORTED_PATCH",
        PluginAdvisorStatus.UnsupportedInteraction => "UNSUPPORTED_INTERACTION",
        _ => status.ToString().ToUpperInvariant()
    };

    private sealed record AutomationAdviceDocument(
        string ProtocolVersion,
        string PluginVersion,
        string RuleSetVersion,
        DateTimeOffset GeneratedAt,
        string GameId,
        string? StateId,
        string Status,
        string? RouteId,
        double Confidence,
        double CoverageProbability,
        double LethalProbability,
        bool AutomationAllowed,
        IReadOnlyList<string> Blockers,
        AutomationStateLayout? Layout,
        IReadOnlyList<AutomationAdviceStep> Steps);

    private sealed record AutomationStateLayout(
        int FriendlyHandCount,
        int FriendlyBoardCount,
        int OpponentBoardCount,
        int ChoiceCount);

    private sealed record AutomationAdviceStep(
        int Index,
        string Type,
        int? SourceEntityId,
        int? TargetEntityId,
        int? BoardPosition,
        int? ChoiceId,
        AutomationEntityLocator? Source,
        AutomationEntityLocator? Target);

    private sealed record AutomationEntityLocator(
        int EntityId,
        string? CardId,
        string Zone,
        int? Index,
        int? Count);
}
