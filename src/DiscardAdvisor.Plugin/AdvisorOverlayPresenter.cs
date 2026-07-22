using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DiscardAdvisor.Rules.Model;
using DiscardAdvisor.Search;

namespace DiscardAdvisor.Plugin;

public static class AdvisorOverlayLayout
{
    public const double PanelWidth = 344;
    public const double PanelHeight = 460;
    public const double HeaderHeight = 52;
    public const double StepHeight = 44;
    public const int DefaultVisibleSteps = 5;
}

public enum OverlayRiskTone
{
    Neutral,
    Positive,
    Caution,
    Critical
}

public sealed class OverlayStep
{
    public OverlayStep(int index, string title, string detail)
    {
        Index = index;
        Title = title;
        Detail = detail;
    }

    public int Index { get; }
    public string Title { get; }
    public string Detail { get; }
}

public sealed class OverlayRoute
{
    public OverlayRoute(
        string routeId,
        string title,
        string confidence,
        string risk,
        OverlayRiskTone riskTone,
        IReadOnlyList<OverlayStep> steps)
    {
        RouteId = routeId;
        Title = title;
        Confidence = confidence;
        Risk = risk;
        RiskTone = riskTone;
        Steps = steps;
    }

    public string RouteId { get; }
    public string Title { get; }
    public string Confidence { get; }
    public string Risk { get; }
    public OverlayRiskTone RiskTone { get; }
    public IReadOnlyList<OverlayStep> Steps { get; }
}

public sealed class AdvisorOverlayState
{
    public AdvisorOverlayState(
        PluginAdvisorStatus status,
        string statusText,
        string detailText,
        OverlayRiskTone statusTone,
        OverlayRoute? primaryRoute = null,
        IReadOnlyList<OverlayRoute>? alternatives = null)
    {
        Status = status;
        StatusText = statusText;
        DetailText = detailText;
        StatusTone = statusTone;
        PrimaryRoute = primaryRoute;
        Alternatives = alternatives ?? Array.Empty<OverlayRoute>();
    }

    public PluginAdvisorStatus Status { get; }
    public string StatusText { get; }
    public string DetailText { get; }
    public OverlayRiskTone StatusTone { get; }
    public OverlayRoute? PrimaryRoute { get; }
    public IReadOnlyList<OverlayRoute> Alternatives { get; }
    public bool HasRoutes => PrimaryRoute is not null;
}

public interface ICardNameResolver
{
    string Resolve(string cardId);
}

public sealed class CardIdNameResolver : ICardNameResolver
{
    public string Resolve(string cardId) => cardId;
}

public sealed class AdvisorOverlayPresenter
{
    private readonly ICardNameResolver _cardNames;

    public AdvisorOverlayPresenter(ICardNameResolver? cardNames = null)
    {
        _cardNames = cardNames ?? new CardIdNameResolver();
    }

    public AdvisorOverlayState Present(PluginAdvisorUpdate update)
    {
        if (update is null)
            throw new ArgumentNullException(nameof(update));
        if (update.Status != PluginAdvisorStatus.Ready || update.State is null || update.Result is null)
            return StatusState(update);
        if (update.Result.Candidates.IsEmpty)
            return new AdvisorOverlayState(
                PluginAdvisorStatus.NoLegalRoute,
                "No legal route",
                "The current state has no supported action sequence.",
                OverlayRiskTone.Caution);

        var routes = update.Result.Candidates.Select((candidate, index) => Route(
                update.State,
                candidate,
                index == 0 ? "Recommended" : "Alternative " + index.ToString(CultureInfo.InvariantCulture)))
            .ToArray();
        return new AdvisorOverlayState(
            PluginAdvisorStatus.Ready,
            update.Result.DeterministicLethalFound ? "Lethal found" : "Recommendation ready",
            "Turn " + update.State.TurnNumber.ToString(CultureInfo.InvariantCulture),
            routes[0].RiskTone,
            routes[0],
            routes.Skip(1).ToArray());
    }

    private AdvisorOverlayState StatusState(PluginAdvisorUpdate update) => update.Status switch
    {
        PluginAdvisorStatus.Analyzing => new AdvisorOverlayState(
            update.Status,
            "Analyzing",
            "Calculating current routes.",
            OverlayRiskTone.Neutral),
        PluginAdvisorStatus.Stale => new AdvisorOverlayState(
            update.Status,
            "State changed",
            "Refreshing the recommendation.",
            OverlayRiskTone.Caution),
        PluginAdvisorStatus.UnsupportedPatch => new AdvisorOverlayState(
            update.Status,
            "Unsupported patch",
            "Local rules are disabled for this build.",
            OverlayRiskTone.Critical),
        PluginAdvisorStatus.UnsupportedInteraction => new AdvisorOverlayState(
            update.Status,
            "Interaction not covered",
            update.Details.FirstOrDefault() ?? "This state cannot be evaluated safely.",
            OverlayRiskTone.Critical),
        PluginAdvisorStatus.NoLegalRoute => new AdvisorOverlayState(
            update.Status,
            "No legal route",
            "No supported action sequence is available.",
            OverlayRiskTone.Caution),
        PluginAdvisorStatus.Inactive => new AdvisorOverlayState(
            update.Status,
            "Inactive",
            "The supported deck and mode are not active.",
            OverlayRiskTone.Neutral),
        _ => new AdvisorOverlayState(
            PluginAdvisorStatus.Offline,
            "Offline",
            "Waiting for a supported game.",
            OverlayRiskTone.Neutral)
    };

    private OverlayRoute Route(
        RuleGameState initialState,
        RiskAwareRouteCandidate candidate,
        string title)
    {
        var steps = candidate.Actions.Select((action, index) => Step(
                initialState,
                candidate.RepresentativeRoute.State,
                action,
                index))
            .ToArray();
        var lethalProbability = candidate.Risk.LethalProbability;
        var riskTone = lethalProbability >= 0.999999d
            ? OverlayRiskTone.Positive
            : candidate.Risk.CoverageProbability < 0.8d || candidate.Confidence < 0.65d
                ? OverlayRiskTone.Critical
                : candidate.Risk.Variance > 1000 || lethalProbability > 0
                    ? OverlayRiskTone.Caution
                    : OverlayRiskTone.Neutral;
        var risk = lethalProbability >= 0.999999d
            ? "Certain lethal"
            : lethalProbability > 0
                ? "Lethal " + Percent(lethalProbability)
                : "P10 " + candidate.Risk.P10.ToString("0", CultureInfo.InvariantCulture) +
                  "  |  Coverage " + Percent(candidate.Risk.CoverageProbability);
        return new OverlayRoute(
            candidate.CandidateId,
            title,
            "Confidence " + Percent(candidate.Confidence),
            risk,
            riskTone,
            steps);
    }

    private OverlayStep Step(
        RuleGameState initialState,
        RuleGameState finalState,
        RuleAction action,
        int index)
    {
        var sourceId = SourceEntityId(initialState, action);
        var targetId = TargetEntityId(action);
        var sourceName = EntityName(initialState, finalState, sourceId);
        var targetName = EntityName(initialState, finalState, targetId);
        var title = action switch
        {
            PlayCardAction => "Play " + sourceName,
            AttackAction => sourceName + " attacks",
            UseHeroPowerAction => "Use hero power",
            UseLocationAction => "Use " + sourceName,
            SelectChoiceAction => "Choose " + targetName,
            EndTurnAction => "End turn",
            _ => "Unsupported action"
        };
        var detail = targetId is null
            ? action is PlayCardAction play && play.BoardPosition is int position
                ? "Position " + position.ToString(CultureInfo.InvariantCulture)
                : string.Empty
            : "Target: " + targetName;
        return new OverlayStep(index + 1, title, detail);
    }

    private string EntityName(RuleGameState initialState, RuleGameState finalState, int? entityId)
    {
        if (entityId is not int id)
            return string.Empty;
        var cardId = FindCardId(initialState, id) ?? FindCardId(finalState, id);
        return cardId is null
            ? "Entity " + id.ToString(CultureInfo.InvariantCulture)
            : _cardNames.Resolve(cardId);
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

    private static string? FindCardId(RuleGameState state, int entityId)
    {
        foreach (var player in new[] { state.Friendly, state.Opponent })
        {
            if (player.Hero.EntityId == entityId)
                return player.Hero.CardId;
            if (player.HeroPower.EntityId == entityId)
                return player.HeroPower.CardId;
            if (player.Weapon?.EntityId == entityId)
                return player.Weapon.CardId;
            var cardId = player.Hand.FirstOrDefault(card => card.EntityId == entityId)?.CardId ??
                         player.Deck.FirstOrDefault(card => card.EntityId == entityId)?.CardId ??
                         player.Board.FirstOrDefault(card => card.EntityId == entityId)?.CardId ??
                         player.Locations.FirstOrDefault(card => card.EntityId == entityId)?.CardId ??
                         player.Graveyard.FirstOrDefault(card => card.EntityId == entityId)?.CardId;
            if (cardId is not null)
                return cardId;
        }
        return null;
    }

    private static string Percent(double value) => Math.Round(Math.Max(0, Math.Min(1, value)) * 100)
        .ToString("0", CultureInfo.InvariantCulture) + "%";
}
