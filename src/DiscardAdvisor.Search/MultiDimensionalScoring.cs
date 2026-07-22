using System;
using System.Linq;
using DiscardAdvisor.Rules;
using DiscardAdvisor.Rules.Model;

namespace DiscardAdvisor.Search;

public sealed record ScoreDimensions(
    double Lethal,
    double Survival,
    double Board,
    double DiscardValue,
    double Resources,
    double TemporaryValue,
    double BoardSpace,
    double DirectDamage,
    double SelfDamage,
    double DukeGrowth,
    double OpponentPressure)
{
    public static ScoreDimensions Zero { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public ScoreDimensions Add(ScoreDimensions other) => new(
        Lethal + other.Lethal,
        Survival + other.Survival,
        Board + other.Board,
        DiscardValue + other.DiscardValue,
        Resources + other.Resources,
        TemporaryValue + other.TemporaryValue,
        BoardSpace + other.BoardSpace,
        DirectDamage + other.DirectDamage,
        SelfDamage + other.SelfDamage,
        DukeGrowth + other.DukeGrowth,
        OpponentPressure + other.OpponentPressure);

    public ScoreDimensions Scale(double factor) => new(
        Lethal * factor,
        Survival * factor,
        Board * factor,
        DiscardValue * factor,
        Resources * factor,
        TemporaryValue * factor,
        BoardSpace * factor,
        DirectDamage * factor,
        SelfDamage * factor,
        DukeGrowth * factor,
        OpponentPressure * factor);
}

public sealed record ScoringWeights(
    double Lethal,
    double Survival,
    double Board,
    double DiscardValue,
    double Resources,
    double TemporaryValue,
    double BoardSpace,
    double DirectDamage,
    double SelfDamage,
    double DukeGrowth,
    double OpponentPressure)
{
    public static ScoringWeights Aggro { get; } = new(100_000, 8, 5, 3, 2, 3, 3, 2, 7, 1, 5);

    public static ScoringWeights Control { get; } = new(100_000, 4, 3, 4, 7, 5, 4, 5, 3, 4, 2);

    public static ScoringWeights Combo { get; } = new(100_000, 3, 4, 3, 3, 3, 2, 7, 2, 2, 8);

    public static ScoringWeights For(OpponentBelief belief)
    {
        if (belief is null)
            throw new ArgumentNullException(nameof(belief));
        return Aggro.Scale(belief.Aggro)
            .Add(Control.Scale(belief.Control))
            .Add(Combo.Scale(belief.Combo));
    }

    public double WeightedSum(ScoreDimensions dimensions) =>
        Lethal * dimensions.Lethal +
        Survival * dimensions.Survival +
        Board * dimensions.Board +
        DiscardValue * dimensions.DiscardValue +
        Resources * dimensions.Resources +
        TemporaryValue * dimensions.TemporaryValue +
        BoardSpace * dimensions.BoardSpace +
        DirectDamage * dimensions.DirectDamage +
        SelfDamage * dimensions.SelfDamage +
        DukeGrowth * dimensions.DukeGrowth +
        OpponentPressure * dimensions.OpponentPressure;

    private ScoringWeights Add(ScoringWeights other) => new(
        Lethal + other.Lethal,
        Survival + other.Survival,
        Board + other.Board,
        DiscardValue + other.DiscardValue,
        Resources + other.Resources,
        TemporaryValue + other.TemporaryValue,
        BoardSpace + other.BoardSpace,
        DirectDamage + other.DirectDamage,
        SelfDamage + other.SelfDamage,
        DukeGrowth + other.DukeGrowth,
        OpponentPressure + other.OpponentPressure);

    private ScoringWeights Scale(double factor) => new(
        Lethal * factor,
        Survival * factor,
        Board * factor,
        DiscardValue * factor,
        Resources * factor,
        TemporaryValue * factor,
        BoardSpace * factor,
        DirectDamage * factor,
        SelfDamage * factor,
        DukeGrowth * factor,
        OpponentPressure * factor);
}

public sealed record DetailedStateScore(ScoreDimensions Dimensions, double Total);

public interface IDetailedStateEvaluator : IStateEvaluator
{
    DetailedStateScore EvaluateDetailed(RuleGameState state, OpponentBelief? belief = null);

    DetailedStateScore EvaluateRoute(RuleGameState initialState, SearchRoute route, OpponentBelief? belief = null);
}

public sealed class MultiDimensionalStateEvaluator : IDetailedStateEvaluator
{
    private readonly OpponentBelief _defaultBelief;

    public MultiDimensionalStateEvaluator(OpponentBelief? defaultBelief = null)
    {
        _defaultBelief = defaultBelief ?? OpponentBelief.Balanced;
    }

    public double Evaluate(RuleGameState state) => EvaluateDetailed(state).Total;

    public DetailedStateScore EvaluateDetailed(RuleGameState state, OpponentBelief? belief = null)
    {
        if (state is null)
            throw new ArgumentNullException(nameof(state));
        belief ??= _defaultBelief;
        var friendly = state.Friendly;
        var opponent = state.Opponent;
        var friendlyEffectiveHealth = friendly.Hero.Health + friendly.Hero.Armor;
        var opponentEffectiveHealth = opponent.Hero.Health + opponent.Hero.Armor;
        var incomingAttack = opponent.Board
            .Where(minion => !minion.Dormant && !minion.Frozen)
            .Sum(minion => Math.Max(0, minion.Attack)) +
            Math.Max(opponent.Hero.Attack, opponent.Weapon?.Attack ?? 0);
        var tauntProtection = friendly.Board
            .Where(minion => minion.Taunt && !minion.Dormant)
            .Sum(minion => Math.Max(0, minion.Health));
        var friendlyBoard = friendly.Board.Sum(minion => BoardValue(minion, true));
        var opponentBoard = opponent.Board.Sum(minion => BoardValue(minion, false));
        var temporaryCards = friendly.Hand.Where(card => card.Temporary).ToArray();
        var availableSlots = CommonRuleEngine.MaximumBoardSize - friendly.BoardCount;
        var summonDemand = friendly.Hand.Sum(SummonSlotDemand);
        var overdrawRisk = Math.Max(0, friendly.Hand.Length - 8);
        var directDamage = friendly.Hand.Sum(DirectDamageValue);
        var futureSelfDamage = friendly.Deck.Count(card => card.CardId == DiscardWarlockCardIds.ShredOfTime) * 3d +
                               friendly.Hand.Count(card => card.CardId == DiscardWarlockCardIds.PartyFiend) * 3d;
        var lowHealthRisk = Math.Max(0, 15 - friendlyEffectiveHealth);
        var faceDamage = Math.Max(0, opponent.Hero.MaxHealth - opponentEffectiveHealth);
        var readyAttack = friendly.Board.Where(CanAttackNow).Sum(minion => Math.Max(0, minion.Attack));
        var overextension = Math.Max(0, friendly.BoardCount - 3) * belief.Control * 2d;

        var dimensions = new ScoreDimensions(
            opponent.Hero.Health <= 0 && friendly.Hero.Health > 0 ? 1 : 0,
            friendly.Hero.Health <= 0
                ? -100
                : friendlyEffectiveHealth - Math.Max(0, incomingAttack - tauntProtection * 0.6d),
            friendlyBoard - opponentBoard,
            friendly.DiscardCount * 0.25d,
            friendly.Hand.Length * 1.5d + friendly.Mana.Available * 0.3d + friendly.Deck.Length * 0.08d -
            friendly.Fatigue * 2d - overdrawRisk * 1.5d,
            -temporaryCards.Sum(card => 1d + card.Cost * 0.5d),
            availableSlots * 0.25d - Math.Max(0, summonDemand - availableSlots) * 2d,
            directDamage,
            -futureSelfDamage * 0.35d - lowHealthRisk,
            DukeGrowthValue(friendly),
            faceDamage + readyAttack * (0.4d + belief.Combo * 0.4d) - overextension);
        return new DetailedStateScore(dimensions, ScoringWeights.For(belief).WeightedSum(dimensions));
    }

    public DetailedStateScore EvaluateRoute(
        RuleGameState initialState,
        SearchRoute route,
        OpponentBelief? belief = null)
    {
        if (initialState is null)
            throw new ArgumentNullException(nameof(initialState));
        if (route is null)
            throw new ArgumentNullException(nameof(route));
        belief ??= _defaultBelief;
        var stateScore = EvaluateDetailed(route.State, belief);
        var discardValue = route.Events
            .Where(ruleEvent => ruleEvent.Type == "discard")
            .Sum(ruleEvent => DiscardOutcomeValue(ruleEvent.CardId));
        var expiredTemporaryCards = route.Events.Count(ruleEvent =>
            ruleEvent.Type == "discard_source" && ruleEvent.CardId == "temporary_expired");
        var failedSummons = route.Events.Count(ruleEvent => ruleEvent.Type == "summon_failed_board_full");
        var unmodeledRandomEffects = route.Events.Count(ruleEvent =>
            ruleEvent.Type is "random_summon_effect_unmodeled" or "random_one_cost_summon_unresolved");
        var initialEffectiveHealth = initialState.Friendly.Hero.Health + initialState.Friendly.Hero.Armor;
        var finalEffectiveHealth = route.State.Friendly.Hero.Health + route.State.Friendly.Hero.Armor;
        var selfDamageTaken = Math.Max(0, initialEffectiveHealth - finalEffectiveHealth);
        var dimensions = stateScore.Dimensions with
        {
            DiscardValue = stateScore.Dimensions.DiscardValue - initialState.Friendly.DiscardCount * 0.25d + discardValue,
            TemporaryValue = stateScore.Dimensions.TemporaryValue - expiredTemporaryCards * 3d,
            BoardSpace = stateScore.Dimensions.BoardSpace - failedSummons * 2d - unmodeledRandomEffects * 0.5d,
            SelfDamage = stateScore.Dimensions.SelfDamage - selfDamageTaken * 0.5d
        };
        return new DetailedStateScore(dimensions, ScoringWeights.For(belief).WeightedSum(dimensions));
    }

    private static double BoardValue(MinionState minion, bool friendly)
    {
        if (minion.Dormant)
            return 0;
        var value = Math.Max(0, minion.Attack) * 1.6d + Math.Max(0, minion.Health);
        if (minion.Taunt)
            value += 1.5d;
        if (minion.Stealth)
            value += 0.75d;
        if (friendly && CanAttackNow(minion))
            value += Math.Max(0, minion.Attack) * 0.4d;
        return value;
    }

    private static bool CanAttackNow(MinionState minion) =>
        !minion.Dormant && !minion.Frozen && minion.Attack > 0 &&
        minion.AttacksThisTurn < minion.MaxAttacksThisTurn &&
        (!minion.SummonedThisTurn || minion.Charge);

    private static int SummonSlotDemand(HandCardState card) => card.CardId switch
    {
        DiscardWarlockCardIds.PartyFiend => 3,
        DiscardWarlockCardIds.DisposableAcolytes => 2,
        DiscardWarlockCardIds.BonewebEgg => 2,
        DiscardWarlockCardIds.SilverwareGolem or DiscardWarlockCardIds.WalkingDead => 1,
        _ => 0
    };

    private static double DirectDamageValue(HandCardState card) => card.CardId switch
    {
        DiscardWarlockCardIds.Soulfire => 4,
        DiscardWarlockCardIds.SoulBarrage => 2.5,
        _ => 0
    };

    private static double DukeGrowthValue(PlayerState player)
    {
        var handAndDeck = player.Hand.Concat(player.Deck)
            .Where(card => card.CardId == DiscardWarlockCardIds.DukeOfBelow)
            .Sum(card => Math.Max(0, card.Attack - 2) + Math.Max(0, card.Health - 2));
        var board = player.Board
            .Where(minion => minion.CardId == DiscardWarlockCardIds.DukeOfBelow)
            .Sum(minion => Math.Max(0, minion.Attack - 2) + Math.Max(0, minion.MaxHealth - 2));
        return (handAndDeck + board) * 0.5d;
    }

    private static double DiscardOutcomeValue(string? cardId)
    {
        return cardId switch
        {
            DiscardWarlockCardIds.HandOfGuldan => 6,
            DiscardWarlockCardIds.BonewebEgg => 4,
            DiscardWarlockCardIds.SilverwareGolem => 4,
            DiscardWarlockCardIds.WalkingDead => 4.5,
            DiscardWarlockCardIds.SoulBarrage => 4,
            DiscardWarlockCardIds.DisposableAcolytes => 3.5,
            null => 0,
            _ => -DiscardCost(cardId)
        };
    }

    private static double DiscardCost(string cardId) =>
        DiscardWarlockCardCatalog.TryCreate(cardId, 0, out var card) && card is not null
            ? Math.Max(1, card.Cost * 0.75d)
            : 2d;
}
