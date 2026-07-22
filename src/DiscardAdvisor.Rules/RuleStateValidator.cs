using System.Collections.Generic;
using System.Linq;
using DiscardAdvisor.Rules.Model;

namespace DiscardAdvisor.Rules;

public static class RuleStateValidator
{
    public static IReadOnlyList<string> Validate(RuleGameState state)
    {
        var errors = new List<string>();
        ValidatePlayer("friendly", state.Friendly, errors);
        ValidatePlayer("opponent", state.Opponent, errors);

        var entityIds = state.Friendly.Hand.Select(card => card.EntityId)
            .Concat(state.Friendly.Deck.Select(card => card.EntityId))
            .Concat(state.Friendly.Board.Select(minion => minion.EntityId))
            .Concat(state.Friendly.Locations.Select(location => location.EntityId))
            .Concat(state.Friendly.Graveyard.Select(card => card.EntityId))
            .Concat(state.Opponent.Hand.Select(card => card.EntityId))
            .Concat(state.Opponent.Deck.Select(card => card.EntityId))
            .Concat(state.Opponent.Board.Select(minion => minion.EntityId))
            .Concat(state.Opponent.Locations.Select(location => location.EntityId))
            .Concat(state.Opponent.Graveyard.Select(card => card.EntityId))
            .Concat(new[]
            {
                state.Friendly.Hero.EntityId,
                state.Friendly.HeroPower.EntityId,
                state.Friendly.Weapon?.EntityId ?? 0,
                state.Opponent.Hero.EntityId,
                state.Opponent.HeroPower.EntityId,
                state.Opponent.Weapon?.EntityId ?? 0
            })
            .Where(id => id > 0)
            .ToArray();
        errors.AddRange(entityIds.GroupBy(id => id).Where(group => group.Count() > 1).Select(group => $"duplicate_entity:{group.Key}"));
        return errors;
    }

    public static bool IsValid(RuleGameState state) => Validate(state).Count == 0;

    private static void ValidatePlayer(string name, PlayerState player, ICollection<string> errors)
    {
        if (player.Hand.Length > CommonRuleEngine.MaximumHandSize)
            errors.Add($"{name}_hand_overflow");
        if (player.BoardCount > CommonRuleEngine.MaximumBoardSize)
            errors.Add($"{name}_board_overflow");
        if (player.Fatigue < 0 || player.Hero.Health < 0 || player.Hero.MaxHealth < 1)
            errors.Add($"{name}_hero_health");
        if (player.DiscardCount < 0)
            errors.Add($"{name}_discard_count");
        if (player.Mana.Available < 0 || player.Mana.Temporary < 0 || player.Mana.Spent < 0 || player.Mana.Maximum < 0)
            errors.Add($"{name}_mana_negative");
        if (player.Mana.Available > player.Mana.Maximum + player.Mana.Temporary)
            errors.Add($"{name}_mana_overflow");
        if (!HasContiguousPositions(player.Board.Select(minion => minion.BoardPosition)
                .Concat(player.Locations.Select(location => location.BoardPosition))))
            errors.Add($"{name}_board_position");
        if (player.Board.Any(minion => minion.Health < 0 || minion.MaxHealth < 1 || minion.AttacksThisTurn < 0) ||
            player.Locations.Any(location => location.Durability < 0 || location.Cooldown < 0))
            errors.Add($"{name}_permanent_state");
    }

    private static bool HasContiguousPositions(IEnumerable<int> positions)
    {
        var values = positions.OrderBy(position => position).ToArray();
        return values.Select((position, index) => position == index + 1).All(value => value);
    }
}
