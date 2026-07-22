using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using DiscardAdvisor.Rules;
using DiscardAdvisor.Rules.Model;

namespace DiscardAdvisor.Search;

public sealed class LegalActionEnumerator
{
    private readonly DiscardWarlockRuleEngine _rules;

    public LegalActionEnumerator()
        : this(new DiscardWarlockRuleEngine())
    {
    }

    public LegalActionEnumerator(DiscardWarlockRuleEngine rules)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
    }

    public ImmutableArray<RuleAction> Enumerate(RuleGameState state)
    {
        if (state is null)
            throw new ArgumentNullException(nameof(state));
        if (state.PendingChoice is not null)
        {
            return state.PendingChoice.Candidates
                .Select(candidate => (RuleAction)new SelectChoiceAction(
                    state.ActiveSide,
                    state.PendingChoice.ChoiceId,
                    candidate.EntityId))
                .Where(action => _rules.Apply(state, action).IsLegal)
                .Distinct()
                .ToImmutableArray();
        }

        var candidates = new List<RuleAction>();
        AddCardActions(state, candidates);
        AddAttackActions(state, candidates);
        AddHeroPowerActions(state, candidates);
        AddLocationActions(state, candidates);
        candidates.Add(new EndTurnAction(state.ActiveSide));
        return candidates
            .Distinct()
            .Where(action => _rules.Apply(state, action).IsLegal)
            .ToImmutableArray();
    }

    private void AddCardActions(RuleGameState state, ICollection<RuleAction> actions)
    {
        var player = state.Player(state.ActiveSide);
        foreach (var card in player.Hand)
        {
            var targets = TargetIds(state, state.ActiveSide, card.TargetKind);
            var positions = card.CardType is RuleCardType.Minion or RuleCardType.Location
                ? Enumerable.Range(1, player.BoardCount + 1).Select(position => (int?)position)
                : new int?[] { null };
            foreach (var target in targets)
                foreach (var position in positions)
                    actions.Add(new PlayCardAction(state.ActiveSide, card.EntityId, target, position));
        }
    }

    private static void AddAttackActions(RuleGameState state, ICollection<RuleAction> actions)
    {
        var player = state.Player(state.ActiveSide);
        var opponent = state.Player(RuleGameState.Other(state.ActiveSide));
        var sources = player.Board.Select(minion => minion.EntityId).Append(player.Hero.EntityId);
        var targets = opponent.Board.Select(minion => minion.EntityId).Append(opponent.Hero.EntityId);
        foreach (var source in sources)
            foreach (var target in targets)
                actions.Add(new AttackAction(state.ActiveSide, source, target));
    }

    private static void AddHeroPowerActions(RuleGameState state, ICollection<RuleAction> actions)
    {
        var power = state.Player(state.ActiveSide).HeroPower;
        foreach (var target in TargetIds(state, state.ActiveSide, power.TargetKind))
            actions.Add(new UseHeroPowerAction(state.ActiveSide, target));
    }

    private static void AddLocationActions(RuleGameState state, ICollection<RuleAction> actions)
    {
        var player = state.Player(state.ActiveSide);
        var characterIds = player.Board.Select(minion => minion.EntityId)
            .Append(player.Hero.EntityId)
            .Concat(state.Player(RuleGameState.Other(state.ActiveSide)).Board.Select(minion => minion.EntityId))
            .Append(state.Player(RuleGameState.Other(state.ActiveSide)).Hero.EntityId);
        foreach (var location in player.Locations.Where(location =>
                     location.CardId != DiscardWarlockCardIds.ChamberOfViscidus))
            foreach (var target in characterIds)
                actions.Add(new UseLocationAction(state.ActiveSide, location.EntityId, target));
    }

    private static IEnumerable<int?> TargetIds(RuleGameState state, PlayerSide sourceSide, TargetKind targetKind)
    {
        if (targetKind == TargetKind.None)
            return new int?[] { null };

        var friendly = state.Player(sourceSide);
        var enemy = state.Player(RuleGameState.Other(sourceSide));
        var friendlyMinions = friendly.Board.Select(minion => (int?)minion.EntityId);
        var enemyMinions = enemy.Board.Select(minion => (int?)minion.EntityId);
        return targetKind switch
        {
            TargetKind.AnyCharacter => friendlyMinions.Append(friendly.Hero.EntityId)
                .Concat(enemyMinions).Append(enemy.Hero.EntityId),
            TargetKind.EnemyCharacter => enemyMinions.Append(enemy.Hero.EntityId),
            TargetKind.FriendlyCharacter => friendlyMinions.Append(friendly.Hero.EntityId),
            TargetKind.EnemyMinion => enemyMinions,
            TargetKind.FriendlyMinion => friendlyMinions,
            _ => Array.Empty<int?>()
        };
    }
}

