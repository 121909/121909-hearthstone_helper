using System;
using System.Windows.Controls;
using DiscardAdvisor.Search;
using Hearthstone_Deck_Tracker.Plugins;

namespace DiscardAdvisor.Plugin;

public sealed class DiscardAdvisorPlugin : IPlugin
{
    private readonly IPluginRuntime _runtime;
    private HdtOverlayController? _overlay;

    public DiscardAdvisorPlugin()
        : this(CreateRuntime())
    {
    }

    internal DiscardAdvisorPlugin(IPluginRuntime runtime)
    {
        _runtime = runtime;
    }

    public string Name => "Discard Advisor";

    public string Description => "Turn-by-turn recommendations for the locked Wild discard-warlock deck.";

    public string ButtonText => "Show / Hide";

    public string Author => "121909";

    public Version Version => new(0, 1, 0);

    public MenuItem MenuItem => null!;

    public void OnLoad()
    {
        _runtime.Start();
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

    private static IPluginRuntime CreateRuntime()
    {
        var mechanics = new SpecialMechanicsTracker();
        return new PluginRuntime(
            new HdtGameContextProvider(),
            new HdtGameEventSource(mechanics),
            new HdtSnapshotObservationFactory(mechanics),
            HdtPluginDiagnostics.Create(),
            new LocalAdvisorService(new LocalTurnAdvisor(new HdtRandomOneCostMinionPool())));
    }
}
