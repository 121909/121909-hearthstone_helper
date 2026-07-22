using System;
using System.Collections.Generic;
using DiscardAdvisor.Rules.Model;

namespace DiscardAdvisor.Rules;

public static class DiscardWarlockCardIds
{
    public const string CursedCatacombs = "TLC_451";
    public const string EntropicContinuity = "TIME_026";
    public const string PartyFiend = "VAC_940";
    public const string Platysaur = "TLC_603";
    public const string Soulfire = "EX1_308";
    public const string Soularium = "BOT_568";
    public const string WickedWhispers = "DMF_119";
    public const string BonewebEgg = "SCH_147";
    public const string DisposableAcolytes = "CATA_499";
    public const string ChamberOfViscidus = "WON_103";
    public const string OcularOccultist = "CATA_490";
    public const string SilverwareGolem = "KAR_205";
    public const string WalkingDead = "RLK_532";
    public const string Chronoclaws = "END_016";
    public const string DukeOfBelow = "CATA_493";
    public const string SoulBarrage = "RLK_534";
    public const string HandOfGuldan = "BT_300";

    public const string ShredOfTime = "TIME_025t";
    public const string Felbeast = "VAC_940t";
    public const string BonewebSpider = "SCH_147t";
    public const string TheCoin = "GAME_005";
    public const string LifeTap = "CS2_056";
}

public static class DiscardWarlockCardCatalog
{
    private static readonly IReadOnlyDictionary<string, CardTemplate> Templates =
        new Dictionary<string, CardTemplate>(StringComparer.Ordinal)
        {
            [DiscardWarlockCardIds.CursedCatacombs] = new(0, RuleCardType.Spell),
            [DiscardWarlockCardIds.EntropicContinuity] = new(1, RuleCardType.Spell),
            [DiscardWarlockCardIds.PartyFiend] = new(1, RuleCardType.Minion, 1, 1),
            [DiscardWarlockCardIds.Platysaur] = new(1, RuleCardType.Minion, 1, 2),
            [DiscardWarlockCardIds.Soulfire] = new(1, RuleCardType.Spell, TargetKind: TargetKind.AnyCharacter),
            [DiscardWarlockCardIds.Soularium] = new(1, RuleCardType.Spell),
            [DiscardWarlockCardIds.WickedWhispers] = new(1, RuleCardType.Spell),
            [DiscardWarlockCardIds.BonewebEgg] = new(2, RuleCardType.Minion, 0, 2),
            [DiscardWarlockCardIds.DisposableAcolytes] = new(2, RuleCardType.Spell),
            [DiscardWarlockCardIds.ChamberOfViscidus] = new(3, RuleCardType.Location, LocationDurability: 2, LocationCooldown: 2),
            [DiscardWarlockCardIds.OcularOccultist] = new(3, RuleCardType.Minion, 3, 6, Taunt: true),
            [DiscardWarlockCardIds.SilverwareGolem] = new(3, RuleCardType.Minion, 3, 4),
            [DiscardWarlockCardIds.WalkingDead] = new(3, RuleCardType.Minion, 2, 5, Taunt: true),
            [DiscardWarlockCardIds.Chronoclaws] = new(4, RuleCardType.Weapon, 4, 3),
            [DiscardWarlockCardIds.DukeOfBelow] = new(4, RuleCardType.Minion, 2, 2, Rush: true),
            [DiscardWarlockCardIds.SoulBarrage] = new(4, RuleCardType.Spell),
            [DiscardWarlockCardIds.HandOfGuldan] = new(6, RuleCardType.Spell),
            [DiscardWarlockCardIds.ShredOfTime] = new(0, RuleCardType.Spell),
            [DiscardWarlockCardIds.Felbeast] = new(1, RuleCardType.Minion, 1, 1),
            [DiscardWarlockCardIds.BonewebSpider] = new(1, RuleCardType.Minion, 2, 1),
            [DiscardWarlockCardIds.TheCoin] = new(0, RuleCardType.Spell)
        };

    public static IReadOnlyCollection<string> TargetCardIds { get; } = new[]
    {
        DiscardWarlockCardIds.CursedCatacombs,
        DiscardWarlockCardIds.EntropicContinuity,
        DiscardWarlockCardIds.PartyFiend,
        DiscardWarlockCardIds.Platysaur,
        DiscardWarlockCardIds.Soulfire,
        DiscardWarlockCardIds.Soularium,
        DiscardWarlockCardIds.WickedWhispers,
        DiscardWarlockCardIds.BonewebEgg,
        DiscardWarlockCardIds.DisposableAcolytes,
        DiscardWarlockCardIds.ChamberOfViscidus,
        DiscardWarlockCardIds.OcularOccultist,
        DiscardWarlockCardIds.SilverwareGolem,
        DiscardWarlockCardIds.WalkingDead,
        DiscardWarlockCardIds.Chronoclaws,
        DiscardWarlockCardIds.DukeOfBelow,
        DiscardWarlockCardIds.SoulBarrage,
        DiscardWarlockCardIds.HandOfGuldan
    };

    public static HandCardState Create(string cardId, int entityId, int? dynamicCost = null, int discardCount = 0)
    {
        if (!Templates.TryGetValue(cardId, out var template))
            throw new KeyNotFoundException($"Unsupported card id: {cardId}");
        var growth = cardId == DiscardWarlockCardIds.DukeOfBelow ? Math.Max(0, discardCount) * 2 : 0;
        return new HandCardState(
            entityId,
            cardId,
            dynamicCost ?? template.Cost,
            template.CardType,
            template.Attack + growth,
            template.Health + growth,
            template.TargetKind,
            template.LocationDurability,
            template.LocationCooldown,
            template.Taunt,
            template.Rush,
            template.Charge);
    }

    public static bool TryCreate(string cardId, int entityId, out HandCardState? card, int? dynamicCost = null, int discardCount = 0)
    {
        if (!Templates.ContainsKey(cardId))
        {
            card = null;
            return false;
        }
        card = Create(cardId, entityId, dynamicCost, discardCount);
        return true;
    }

    private sealed record CardTemplate(
        int Cost,
        RuleCardType CardType,
        int Attack = 0,
        int Health = 0,
        TargetKind TargetKind = TargetKind.None,
        int LocationDurability = 0,
        int LocationCooldown = 0,
        bool Taunt = false,
        bool Rush = false,
        bool Charge = false);
}
