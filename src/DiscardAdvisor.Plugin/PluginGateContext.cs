using System;
using System.Collections.Generic;
using DiscardAdvisor.Domain;

namespace DiscardAdvisor.Plugin;

public sealed class PluginGateContext
{
    public PluginGateContext(string gameMode, IEnumerable<string?> deckCardIds, RuntimeCompatibility compatibility)
    {
        GameMode = gameMode ?? throw new ArgumentNullException(nameof(gameMode));
        DeckCardIds = deckCardIds ?? throw new ArgumentNullException(nameof(deckCardIds));
        Compatibility = compatibility ?? throw new ArgumentNullException(nameof(compatibility));
    }

    public string GameMode { get; }

    public IEnumerable<string?> DeckCardIds { get; }

    public RuntimeCompatibility Compatibility { get; }
}

public interface IGameContextProvider
{
    PluginGateContext CaptureGateContext();
}

