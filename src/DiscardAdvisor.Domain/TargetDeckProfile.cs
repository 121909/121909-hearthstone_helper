using System.Collections.Generic;

namespace DiscardAdvisor.Domain;

public static class TargetDeckProfile
{
    public const string ProfileId = "wild-discard-warlock";
    public const string GameMode = "RANKED_WILD";
    public const string PlayerClass = "WARLOCK";
    public const int DeckSize = 30;
    public const string DeckHash = "c980187b4a4ff17509f32aa68db749fc6bcb64cc6f8bd32dd61f0d49b8fd2eb0";
    public const string RuleSetVersion = "0.1.0";

    public static IReadOnlyList<DeckCardCount> Cards { get; } = new[]
    {
        new DeckCardCount("BOT_568", 1),
        new DeckCardCount("BT_300", 2),
        new DeckCardCount("CATA_490", 2),
        new DeckCardCount("CATA_493", 2),
        new DeckCardCount("CATA_499", 2),
        new DeckCardCount("DMF_119", 2),
        new DeckCardCount("END_016", 2),
        new DeckCardCount("EX1_308", 1),
        new DeckCardCount("KAR_205", 1),
        new DeckCardCount("RLK_532", 2),
        new DeckCardCount("RLK_534", 2),
        new DeckCardCount("SCH_147", 2),
        new DeckCardCount("TIME_026", 2),
        new DeckCardCount("TLC_451", 2),
        new DeckCardCount("TLC_603", 2),
        new DeckCardCount("VAC_940", 2),
        new DeckCardCount("WON_103", 1)
    };
}

