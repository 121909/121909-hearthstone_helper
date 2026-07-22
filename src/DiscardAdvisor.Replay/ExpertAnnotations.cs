using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using DiscardAdvisor.Rules.Model;

namespace DiscardAdvisor.Replay;

public sealed class ExpertAnnotation
{
    public ExpertAnnotation(string protocolVersion, string stateId, IEnumerable<AnnotatedRoute> expertTop3)
    {
        ProtocolVersion = protocolVersion;
        StateId = stateId;
        ExpertTop3 = expertTop3?.ToImmutableArray() ?? ImmutableArray<AnnotatedRoute>.Empty;
    }

    public string ProtocolVersion { get; }

    public string StateId { get; }

    public ImmutableArray<AnnotatedRoute> ExpertTop3 { get; }

    public void Validate()
    {
        if (!string.Equals(ProtocolVersion, "1.0.0", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported annotation protocol '{ProtocolVersion}'.");
        if (string.IsNullOrWhiteSpace(StateId))
            throw new InvalidOperationException("Annotation state_id is required.");
        if (ExpertTop3.IsEmpty || ExpertTop3.Length > 3)
            throw new InvalidOperationException("Annotation expertTop3 must contain one to three routes.");
        if (ExpertTop3.Any(route => string.IsNullOrWhiteSpace(route.Label)))
            throw new InvalidOperationException("Every annotated route must include a label.");
        if (ExpertTop3.Any(route => route.Actions.IsEmpty))
            throw new InvalidOperationException("Every annotated route must contain at least one action.");
    }
}

public sealed class AnnotatedRoute
{
    public AnnotatedRoute(string label, IEnumerable<AnnotatedAction> actions, string? reason = null)
    {
        Label = label;
        Actions = actions?.ToImmutableArray() ?? ImmutableArray<AnnotatedAction>.Empty;
        Reason = reason;
    }

    public string Label { get; }

    public ImmutableArray<AnnotatedAction> Actions { get; }

    public string? Reason { get; }
}

public sealed record AnnotatedAction(
    string Kind,
    int? SourceEntityId = null,
    int? TargetEntityId = null,
    int? BoardPosition = null,
    int? ChoiceId = null)
{
    public static AnnotatedAction FromRuleAction(RuleAction action) => action switch
    {
        PlayCardAction play => new AnnotatedAction(
            "PLAY_CARD",
            play.SourceEntityId,
            play.TargetEntityId,
            play.BoardPosition),
        AttackAction attack => new AnnotatedAction("ATTACK", attack.SourceEntityId, attack.TargetEntityId),
        UseHeroPowerAction heroPower => new AnnotatedAction("USE_HERO_POWER", TargetEntityId: heroPower.TargetEntityId),
        UseLocationAction location => new AnnotatedAction("USE_LOCATION", location.SourceEntityId, location.SelectedEntityId),
        SelectChoiceAction choice => new AnnotatedAction(
            "SELECT_CHOICE",
            TargetEntityId: choice.SelectedEntityId,
            ChoiceId: choice.ChoiceId),
        EndTurnAction => new AnnotatedAction("END_TURN"),
        _ => new AnnotatedAction("UNSUPPORTED")
    };

    public bool Matches(RuleAction action) => this == FromRuleAction(action);
}
