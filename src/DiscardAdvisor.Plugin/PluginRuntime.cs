using System;
using DiscardAdvisor.Domain;

namespace DiscardAdvisor.Plugin;

public sealed class PluginRuntime : IPluginRuntime, IDisposable
{
    private readonly PluginLifetime _lifetime;
    private readonly IGameContextProvider? _gameContextProvider;
    private readonly DeckSupportGate _supportGate = new();

    public PluginRuntime()
        : this(new PluginLifetime(), null)
    {
    }

    public PluginRuntime(IGameContextProvider gameContextProvider)
        : this(new PluginLifetime(), gameContextProvider)
    {
    }

    internal PluginRuntime(PluginLifetime lifetime, IGameContextProvider? gameContextProvider = null)
    {
        _lifetime = lifetime;
        _gameContextProvider = gameContextProvider;
    }

    public PluginRunState State => _lifetime.State;

    public GateDecision? CurrentGateDecision { get; private set; }

    public void Start()
    {
        _lifetime.Start();
        RefreshEligibility();
    }

    public void Stop()
    {
        _lifetime.Stop();
        CurrentGateDecision = null;
    }

    public void Update()
    {
        // HDT calls this on its update thread. Future work is limited to cheap dirty-state checks here.
        _lifetime.TryGetSession(out _);
    }

    public void RefreshEligibility()
    {
        if (_gameContextProvider is null || !_lifetime.TryGetSession(out _))
            return;

        var context = _gameContextProvider.CaptureGateContext();
        CurrentGateDecision = _supportGate.Evaluate(context.GameMode, context.DeckCardIds, context.Compatibility);
    }

    public void Dispose() => _lifetime.Dispose();
}
