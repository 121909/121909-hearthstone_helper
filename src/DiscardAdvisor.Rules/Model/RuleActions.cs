namespace DiscardAdvisor.Rules.Model;

public abstract record RuleAction(PlayerSide Side);

public sealed record PlayCardAction(PlayerSide Side, int SourceEntityId, int? TargetEntityId = null, int? BoardPosition = null)
    : RuleAction(Side);

public sealed record AttackAction(PlayerSide Side, int SourceEntityId, int TargetEntityId)
    : RuleAction(Side);

public sealed record UseHeroPowerAction(PlayerSide Side, int? TargetEntityId = null)
    : RuleAction(Side);

public sealed record UseLocationAction(PlayerSide Side, int SourceEntityId, int SelectedEntityId)
    : RuleAction(Side);

public sealed record SelectChoiceAction(PlayerSide Side, int ChoiceId, int SelectedEntityId)
    : RuleAction(Side);

public sealed record EndTurnAction(PlayerSide Side) : RuleAction(Side);

