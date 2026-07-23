using System;
using System.Collections.Generic;

namespace DiscardAdvisor.Domain;

public enum GateStatus
{
    Enabled,
    UnsupportedMode,
    IncompleteDeck,
    DeckMismatch,
    UnsupportedPatch,
    UnsupportedHdtVersion,
    UnsupportedCardDefinitions,
    UnsupportedHearthDb
}

public sealed class GateDecision
{
    public GateDecision(
        GateStatus status,
        string? observedDeckHash = null,
        RuntimeCompatibility? observedCompatibility = null)
    {
        Status = status;
        ObservedDeckHash = observedDeckHash;
        ObservedCompatibility = observedCompatibility;
    }

    public GateStatus Status { get; }

    public string? ObservedDeckHash { get; }

    public RuntimeCompatibility? ObservedCompatibility { get; }

    public bool IsEnabled => Status == GateStatus.Enabled;
}

public sealed class DeckSupportGate
{
    public GateDecision Evaluate(
        string gameMode,
        IEnumerable<string?> deckCardIds,
        RuntimeCompatibility compatibility)
    {
        if (!string.Equals(gameMode, TargetDeckProfile.GameMode, StringComparison.Ordinal))
            return new GateDecision(GateStatus.UnsupportedMode, observedCompatibility: compatibility);

        if (!DeckFingerprint.TryCreate(deckCardIds, out var fingerprint) || fingerprint is null || fingerprint.DeckSize != TargetDeckProfile.DeckSize)
            return new GateDecision(GateStatus.IncompleteDeck, fingerprint?.Hash, compatibility);

        if (!string.Equals(fingerprint.Hash, TargetDeckProfile.DeckHash, StringComparison.Ordinal))
            return new GateDecision(GateStatus.DeckMismatch, fingerprint.Hash, compatibility);

        if (compatibility.HearthstoneBuild != TargetRuntimeCompatibility.HearthstoneBuild)
            return new GateDecision(GateStatus.UnsupportedPatch, fingerprint.Hash, compatibility);

        if (!string.Equals(compatibility.HdtVersion, TargetRuntimeCompatibility.HdtVersion, StringComparison.Ordinal))
            return new GateDecision(GateStatus.UnsupportedHdtVersion, fingerprint.Hash, compatibility);

        if (!RuntimeCompatibilityResolver.IsSupportedCardDefsHash(compatibility.CardDefsSha256))
            return new GateDecision(GateStatus.UnsupportedCardDefinitions, fingerprint.Hash, compatibility);

        if (!string.Equals(compatibility.HearthDbSha256, TargetRuntimeCompatibility.HearthDbSha256, StringComparison.OrdinalIgnoreCase))
            return new GateDecision(GateStatus.UnsupportedHearthDb, fingerprint.Hash, compatibility);

        return new GateDecision(GateStatus.Enabled, fingerprint.Hash, compatibility);
    }
}
