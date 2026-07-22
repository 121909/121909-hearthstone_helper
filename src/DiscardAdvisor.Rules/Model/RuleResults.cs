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

public sealed record TransitionResult(
    bool IsLegal,
    RuleGameState State,
    RuleError Error,
    ImmutableArray<RuleEvent> Events)
{
    public static TransitionResult Legal(RuleGameState state, IEnumerable<RuleEvent> events) =>
        new(true, state, RuleError.None, events.ToImmutableArray());

    public static TransitionResult Illegal(RuleGameState state, RuleError error) =>
        new(false, state, error, ImmutableArray<RuleEvent>.Empty);
}

