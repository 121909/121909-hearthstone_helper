using System;
using DiscardAdvisor.Rules.Model;

namespace DiscardAdvisor.Rules;

public sealed record MinionDamageResult(
    MinionState Minion,
    int DamageApplied,
    bool DivineShieldLost);

public sealed record HeroDamageResult(HeroState Hero, int DamageApplied);

public static class RuleDamage
{
    public static MinionDamageResult Apply(MinionState minion, int amount, bool poisonous = false)
    {
        if (amount <= 0 || minion.Immune)
            return new MinionDamageResult(minion, 0, false);
        if (minion.DivineShield)
            return new MinionDamageResult(minion with { DivineShield = false }, 0, true);

        return new MinionDamageResult(
            minion with { Health = poisonous ? 0 : minion.Health - amount },
            amount,
            false);
    }

    public static HeroDamageResult Apply(HeroState hero, int amount)
    {
        if (amount <= 0 || hero.Immune)
            return new HeroDamageResult(hero, 0);
        var armorDamage = Math.Min(hero.Armor, amount);
        return new HeroDamageResult(
            hero with
            {
                Armor = hero.Armor - armorDamage,
                Health = Math.Max(0, hero.Health - (amount - armorDamage))
            },
            amount);
    }

    public static HeroState Heal(HeroState hero, int amount) => amount <= 0
        ? hero
        : hero with { Health = Math.Min(hero.MaxHealth, hero.Health + amount) };
}
