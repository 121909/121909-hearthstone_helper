using System;
using System.IO;
using System.Windows.Controls;
using DiscardAdvisor.Search;
using Hearthstone_Deck_Tracker;
using Hearthstone_Deck_Tracker.API;
using Hearthstone_Deck_Tracker.Plugins;

namespace DiscardAdvisor.Plugin;

public sealed class DiscardAdvisorPlugin : IPlugin
{
    public const string SemanticVersion = "0.4.7";

    private readonly IPluginRuntime _runtime;
    private readonly PluginSettings _settings;
    private HdtOverlayController? _overlay;

    public DiscardAdvisorPlugin()
    {
        _settings = PluginSettings.Load(Path.Combine(
            Config.Instance.DataDir,
            "DiscardAdvisor",
            "settings.json"));
        _runtime = CreateRuntime(_settings);
    }

    internal DiscardAdvisorPlugin(IPluginRuntime runtime)
    {
        _runtime = runtime;
        _settings = PluginSettings.Experimental;
    }

    public string Name => "Discard Advisor";

    public string Description => "Turn-by-turn recommendations for the locked Wild discard-warlock deck.";

    public string ButtonText => _settings.ShowOverlay ? "Show / Hide" : "Shadow mode";

    public string Author => "121909";

    public Version Version => Version.Parse(SemanticVersion);

    public MenuItem MenuItem => null!;

    public void OnLoad()
    {
        _runtime.Start();
        if (!_settings.ShowOverlay)
            return;
        _overlay = new HdtOverlayController(_runtime as IOverlayStateSource);
        _overlay.Attach();
    }

    public void OnUnload()
    {
        _overlay?.Dispose();
        _overlay = null;
        _runtime.Stop();
    }

    public void OnButtonPress()
    {
        _overlay?.ToggleVisibility();
    }

    public void OnUpdate() => _runtime.Update();

    internal static void RegisterGameEvents(HdtGameEventRegistration registration)
    {
        // ActionList attributes callbacks to the immediate Add() caller type.
        // Register here so HDT can remove them when this plugin is disabled.
        GameEvents.OnGameStart.Add(registration.NotifyGameStarted);
        GameEvents.OnGameEnd.Add(registration.NotifyGameEnded);
        GameEvents.OnTurnStart.Add(_ => registration.NotifyStateChanged());
        GameEvents.OnPlayerDraw.Add(registration.NotifyPlayerDraw);
        GameEvents.OnPlayerGet.Add(registration.NotifyPlayerGet);
        GameEvents.OnPlayerPlay.Add(registration.NotifyPlayerPlay);
        GameEvents.OnPlayerHandDiscard.Add(registration.NotifyPlayerDiscard);
        GameEvents.OnOpponentPlay.Add(_ => registration.NotifyStateChanged());
        GameEvents.OnOpponentHandDiscard.Add(_ => registration.NotifyStateChanged());
        GameEvents.OnPlayerMinionAttack.Add(_ => registration.NotifyStateChanged());
        GameEvents.OnOpponentMinionAttack.Add(_ => registration.NotifyStateChanged());
        GameEvents.OnEntityWillTakeDamage.Add(_ => registration.NotifyStateChanged());
        GameEvents.OnModeChanged.Add(_ => registration.NotifyStateChanged());
    }

    private static IPluginRuntime CreateRuntime(PluginSettings settings)
    {
        var mechanics = new SpecialMechanicsTracker();
        return new PluginRuntime(
            new HdtGameContextProvider(),
            new HdtGameEventSource(mechanics),
            new HdtSnapshotObservationFactory(mechanics),
            HdtPluginDiagnostics.Create(settings.ModeName),
            new LocalAdvisorService(new LocalTurnAdvisor(new HdtRandomOneCostMinionPool())));
    }
}
