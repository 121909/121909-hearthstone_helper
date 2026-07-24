using System;
using System.Collections.Generic;
using System.Linq;
using DiscardAdvisor.Search;
using HearthDb;
using HearthDb.Enums;

namespace DiscardAdvisor.Plugin;

public sealed class HdtRandomOneCostMinionPool : IRandomOneCostMinionPool
{
    private static readonly HashSet<string> ModeledMechanics = new(StringComparer.OrdinalIgnoreCase)
    {
        "Battlecry",
        "Charge",
        "DivineShield",
        "Lifesteal",
        "Poisonous",
        "Rush",
        "Stealth",
        "Taunt"
    };

    public HdtRandomOneCostMinionPool()
    {
        Version = Cards.Build;
        Candidates = Cards.Collectible.Values
            .Where(card => card.Type == CardType.MINION && card.Cost == 1)
            .Where(card => !string.IsNullOrWhiteSpace(card.Id))
            .OrderBy(card => card.Id, StringComparer.Ordinal)
            .Select(CreateCandidate)
            .ToArray();
    }

    public string Version { get; }

    public IReadOnlyList<RandomOneCostMinion> Candidates { get; }

    private static RandomOneCostMinion CreateCandidate(Card card)
    {
        var mechanics = card.Mechanics ?? Array.Empty<string>();
        return new RandomOneCostMinion(
            card.Id,
            card.Attack,
            card.Health,
            card.Taunt,
            HasMechanic(mechanics, "Rush"),
            HasMechanic(mechanics, "Charge"),
            HasMechanic(mechanics, "Stealth"),
            card.DivineShield,
            card.Poisonous,
            HasMechanic(mechanics, "Lifesteal"),
            HasUnmodeledEffects(card.Text, mechanics));
    }

    private static bool HasMechanic(IEnumerable<string> mechanics, string expected) =>
        mechanics.Any(mechanic => string.Equals(mechanic, expected, StringComparison.OrdinalIgnoreCase));

    private static bool HasUnmodeledEffects(string text, IEnumerable<string> mechanics) =>
        !string.IsNullOrWhiteSpace(text) &&
        (mechanics.Any(mechanic => !ModeledMechanics.Contains(mechanic)) || !mechanics.Any());
}
