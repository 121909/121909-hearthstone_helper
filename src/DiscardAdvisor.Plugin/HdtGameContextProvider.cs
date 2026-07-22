using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using DiscardAdvisor.Domain;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker;
using Hearthstone_Deck_Tracker.Enums;
using HdtApiCore = Hearthstone_Deck_Tracker.API.Core;

namespace DiscardAdvisor.Plugin;

internal sealed class HdtGameContextProvider : IGameContextProvider
{
    public PluginGateContext CaptureGateContext()
    {
        var game = HdtApiCore.Game;
        var deck = DeckList.Instance.ActiveDeckVersion;
        var cardIds = deck?.Cards.SelectMany(card => Repeat(card.Id, card.Count)).ToArray()
            ?? Array.Empty<string>();
        var gameMode = game.CurrentGameType == GameType.GT_RANKED && game.CurrentFormat == Format.Wild
            ? TargetDeckProfile.GameMode
            : $"{game.CurrentGameType}:{game.CurrentFormat}";

        return new PluginGateContext(gameMode, cardIds, CaptureCompatibility());
    }

    internal static RuntimeCompatibility CaptureCompatibility()
    {
        var hdtAssembly = typeof(HdtApiCore).Assembly;
        var hdtVersion = hdtAssembly.GetName().Version?.ToString(3) ?? string.Empty;
        var hearthDbAssembly = typeof(HearthDb.Cards).Assembly;
        var cardDefsPath = Path.Combine(Config.AppDataPath, "CardDefs", "CardDefs.base.xml");
        var cardDefsHash = File.Exists(cardDefsPath)
            ? ComputeSha256(cardDefsPath)
            : TargetRuntimeCompatibility.CardDefsSha256;

        var hearthstoneBuild = HdtApiCore.Game.MetaData.HearthstoneBuild
            ?? (int.TryParse(HearthDb.Cards.Build, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedBuild) ? parsedBuild : 0);

        return new RuntimeCompatibility(
            hearthstoneBuild,
            hdtVersion,
            cardDefsHash,
            ComputeSha256(hearthDbAssembly.Location));
    }

    private static IEnumerable<string> Repeat(string cardId, int count)
    {
        for (var index = 0; index < count; index++)
            yield return cardId;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        return string.Concat(sha256.ComputeHash(stream).Select(value => value.ToString("x2")));
    }
}
