using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DiscardAdvisor.Rules.Model;

namespace DiscardAdvisor.Search;

public static class RuleStateKey
{
    public static string Calculate(RuleGameState state)
    {
        if (state is null)
            throw new ArgumentNullException(nameof(state));
        var canonical = new StringBuilder();
        canonical.Append(state.TurnNumber).Append('|').Append((int)state.ActiveSide).Append('|').Append(state.NextEntityId).Append('|');
        AppendPlayer(canonical, state.Friendly);
        AppendPlayer(canonical, state.Opponent);
        if (state.PendingChoice is not null)
        {
            canonical.Append("choice:").Append(state.PendingChoice.ChoiceId).Append(':')
                .Append(state.PendingChoice.ChoiceType).Append(':').Append(state.PendingChoice.SourceCardId).Append(':')
                .Append(state.PendingChoice.SourceEntityId).Append('|');
            foreach (var candidate in state.PendingChoice.Candidates.OrderBy(candidate => candidate.EntityId))
                canonical.Append(candidate).Append('|');
        }
        foreach (var binding in state.Bindings.OrderBy(binding => binding.Key))
            canonical.Append("binding:").Append(binding.Key).Append(':').Append(binding.Value).Append('|');
        using var sha256 = SHA256.Create();
        return string.Concat(sha256.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString()))
            .Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
    }

    internal static string TacticalSignature(RuleGameState state)
    {
        var active = state.Player(state.ActiveSide);
        var normalizedMana = active.Mana with
        {
            Available = 0,
            Temporary = 0,
            Spent = 0
        };
        return Calculate(state.WithPlayer(state.ActiveSide, active with { Mana = normalizedMana }));
    }

    private static void AppendPlayer(StringBuilder canonical, PlayerState player)
    {
        canonical.Append(player.Hero).Append('|').Append(player.HeroPower).Append('|')
            .Append(player.Mana).Append('|').Append(player.Weapon).Append('|')
            .Append(player.Fatigue).Append('|').Append(player.DiscardCount).Append('|')
            .Append(player.DeckOrderKnown).Append('|');
        AppendSequence(canonical, "hand", player.Hand.OrderBy(card => card.EntityId));
        AppendSequence(canonical, "board", player.Board.OrderBy(minion => minion.BoardPosition));
        AppendSequence(canonical, "locations", player.Locations.OrderBy(location => location.BoardPosition));
        AppendSequence(canonical, "deck", player.Deck);
        AppendSequence(canonical, "graveyard", player.Graveyard.OrderBy(card => card.EntityId));
    }

    private static void AppendSequence<T>(StringBuilder canonical, string name, System.Collections.Generic.IEnumerable<T> values)
    {
        canonical.Append(name).Append('[');
        foreach (var value in values)
            canonical.Append(value).Append(';');
        canonical.Append("]|");
    }
}
