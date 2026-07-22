using System;
using System.Linq;
using DiscardAdvisor.Rules.Model;

namespace DiscardAdvisor.Search;

public enum OpponentArchetype
{
    Aggro,
    Control,
    Combo
}

public sealed record OpponentBelief
{
    public OpponentBelief(double aggro, double control, double combo)
    {
        ValidateProbability(aggro, nameof(aggro));
        ValidateProbability(control, nameof(control));
        ValidateProbability(combo, nameof(combo));
        var total = aggro + control + combo;
        if (total <= 0)
            throw new ArgumentException("At least one opponent probability must be positive.");

        Aggro = aggro / total;
        Control = control / total;
        Combo = combo / total;
    }

    public static OpponentBelief Balanced { get; } = new(1, 1, 1);

    public double Aggro { get; }

    public double Control { get; }

    public double Combo { get; }

    public OpponentArchetype MostLikely => new[]
        {
            (Archetype: OpponentArchetype.Aggro, Probability: Aggro),
            (Archetype: OpponentArchetype.Control, Probability: Control),
            (Archetype: OpponentArchetype.Combo, Probability: Combo)
        }
        .OrderByDescending(entry => entry.Probability)
        .ThenBy(entry => entry.Archetype)
        .First().Archetype;

    public double Probability(OpponentArchetype archetype) => archetype switch
    {
        OpponentArchetype.Aggro => Aggro,
        OpponentArchetype.Control => Control,
        OpponentArchetype.Combo => Combo,
        _ => throw new ArgumentOutOfRangeException(nameof(archetype))
    };

    private static void ValidateProbability(double probability, string parameterName)
    {
        if (double.IsNaN(probability) || double.IsInfinity(probability) || probability < 0)
            throw new ArgumentOutOfRangeException(parameterName);
    }
}

public sealed record OpponentEvidence(
    string? OpponentClass = null,
    int TurnNumber = 1,
    int BoardAttack = 0,
    int BoardMinions = 0,
    int RevealedLowCostCards = 0,
    int RevealedRemovalCards = 0,
    int RevealedComboCards = 0,
    int OpponentHandSize = 0,
    int CardsPlayed = 0);

public sealed class OpponentBeliefModel
{
    public OpponentBelief Estimate(OpponentEvidence evidence) => Update(OpponentBelief.Balanced, evidence);

    public OpponentBelief Estimate(RuleGameState state)
    {
        if (state is null)
            throw new ArgumentNullException(nameof(state));
        var opponent = state.Opponent;
        return Estimate(new OpponentEvidence(
            TurnNumber: state.TurnNumber,
            BoardAttack: opponent.Board.Where(minion => !minion.Dormant).Sum(minion => Math.Max(0, minion.Attack)),
            BoardMinions: opponent.Board.Count(minion => !minion.Dormant)));
    }

    public OpponentBelief Update(OpponentBelief prior, OpponentEvidence evidence)
    {
        if (prior is null)
            throw new ArgumentNullException(nameof(prior));
        if (evidence is null)
            throw new ArgumentNullException(nameof(evidence));

        var turn = Math.Max(1, evidence.TurnNumber);
        var tempo = Math.Max(0, evidence.BoardAttack) / (double)turn;
        var classPrior = ClassPrior(evidence.OpponentClass);
        var aggroLikelihood = 1d + classPrior.Aggro * 1.5d +
                              Math.Max(0, evidence.RevealedLowCostCards) * 0.35d +
                              Math.Max(0, evidence.BoardMinions) * 0.12d + tempo * 0.4d +
                              Math.Max(0, evidence.CardsPlayed - turn) * 0.08d;
        var controlLikelihood = 1d + classPrior.Control * 1.5d +
                                Math.Max(0, evidence.RevealedRemovalCards) * 0.45d +
                                Math.Max(0, evidence.OpponentHandSize - 5) * 0.08d +
                                Math.Max(0, turn - 5) * 0.05d;
        var comboLikelihood = 1d + classPrior.Combo * 1.5d +
                              Math.Max(0, evidence.RevealedComboCards) * 0.65d +
                              Math.Max(0, turn - 4) * 0.07d +
                              Math.Max(0, 1.5d - tempo) * 0.12d;

        return new OpponentBelief(
            prior.Aggro * aggroLikelihood,
            prior.Control * controlLikelihood,
            prior.Combo * comboLikelihood);
    }

    private static OpponentBelief ClassPrior(string? opponentClass)
    {
        var normalized = (opponentClass ?? string.Empty)
            .Replace("_", string.Empty)
            .Replace(" ", string.Empty)
            .ToUpperInvariant();
        return normalized switch
        {
            "DEMONHUNTER" or "HUNTER" or "PALADIN" or "SHAMAN" => new OpponentBelief(0.58, 0.18, 0.24),
            "PRIEST" or "WARRIOR" => new OpponentBelief(0.18, 0.58, 0.24),
            "DRUID" or "MAGE" or "ROGUE" or "WARLOCK" => new OpponentBelief(0.2, 0.32, 0.48),
            _ => OpponentBelief.Balanced
        };
    }
}
