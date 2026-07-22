using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace DiscardAdvisor.Search;

public sealed class DominancePruner
{
    public ImmutableArray<SearchRoute> Prune(IEnumerable<SearchRoute> routes, out int prunedCount)
    {
        var kept = ImmutableArray.CreateBuilder<SearchRoute>();
        prunedCount = 0;
        foreach (var group in routes.GroupBy(route => RuleStateKey.TacticalSignature(route.State)))
        {
            var groupKept = new List<SearchRoute>();
            foreach (var route in group.OrderByDescending(route => route.State.Player(route.State.ActiveSide).Mana.Available)
                         .ThenByDescending(route => route.Score))
            {
                var mana = route.State.Player(route.State.ActiveSide).Mana;
                var dominated = groupKept.Any(other =>
                {
                    var otherMana = other.State.Player(other.State.ActiveSide).Mana;
                    return otherMana.Available >= mana.Available &&
                           otherMana.Temporary >= mana.Temporary &&
                           other.Score >= route.Score;
                });
                if (dominated)
                    prunedCount++;
                else
                    groupKept.Add(route);
            }
            kept.AddRange(groupKept);
        }
        return kept.ToImmutable();
    }
}

