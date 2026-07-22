using System;

namespace DiscardAdvisor.Plugin;

public sealed class PluginRuntime : IPluginRuntime, IDisposable
{
    private readonly PluginLifetime _lifetime;

    public PluginRuntime()
        : this(new PluginLifetime())
    {
    }

    internal PluginRuntime(PluginLifetime lifetime)
    {
        _lifetime = lifetime;
    }

    public PluginRunState State => _lifetime.State;

    public void Start() => _lifetime.Start();

    public void Stop() => _lifetime.Stop();

    public void Update()
    {
        // HDT calls this on its update thread. Future work is limited to cheap dirty-state checks here.
        _lifetime.TryGetSession(out _);
    }

    public void Dispose() => _lifetime.Dispose();
}

