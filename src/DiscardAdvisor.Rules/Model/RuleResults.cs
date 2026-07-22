using System.Collections.Generic;
using System.Collections.Immutable;

namespace DiscardAdvisor.Rules.Model;

public enum RuleError
{
    None,
    NotActivePlayer,
    SourceNotFound,
    TargetRequired,
    InvalidTarget,
    InsufficientMana,
    BoardFull,
    InvalidBoardPosition,
    Exhausted,
    Frozen,
    Dormant,
    TauntBlocksTarget,
    StealthedTarget,
    RushCannotAttackHero,
    LocationUnavailable,
    UnsupportedAction
}

public sealed record RuleEvent(string Type, int? SourceEntityId = null, int? TargetEntityId = null, int Amount = 0, string? CardId = null);

public sealed record RuleBranch(
    string OutcomeId,
    double Probability,
    RuleGameState State,
    ImmutableArray<RuleEvent> Events);

public sealed record TransitionResult(
    bool IsLegal,
    RuleGameState State,
    RuleError Error,
    ImmutableArray<RuleEvent> Events,
    ImmutableArray<RuleBranch> Branches)
{
    public static TransitionResult Legal(RuleGameState state, IEnumerable<RuleEvent> events) =>
        new(true, state, RuleError.None, events.ToImmutableArray(), ImmutableArray<RuleBranch>.Empty);

    public static TransitionResult Illegal(RuleGameState state, RuleError error) =>
        new(false, state, error, ImmutableArray<RuleEvent>.Empty, ImmutableArray<RuleBranch>.Empty);
}
