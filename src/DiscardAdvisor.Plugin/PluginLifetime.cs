using System;
using System.Threading;

namespace DiscardAdvisor.Plugin;

public enum PluginRunState
{
    Stopped,
    Running
}

public sealed class PluginLifetime : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _session;
    private long _generation;

    public PluginRunState State
    {
        get
        {
            lock (_gate)
                return _session is null ? PluginRunState.Stopped : PluginRunState.Running;
        }
    }

    public long Generation
    {
        get
        {
            lock (_gate)
                return _generation;
        }
    }

    public CancellationToken Start()
    {
        lock (_gate)
        {
            if (_session is not null)
                return _session.Token;

            _session = new CancellationTokenSource();
            _generation++;
            return _session.Token;
        }
    }

    public void Stop()
    {
        CancellationTokenSource? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }

        if (session is null)
            return;

        session.Cancel();
        session.Dispose();
    }

    public bool TryGetSession(out CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_session is null)
            {
                cancellationToken = default;
                return false;
            }

            cancellationToken = _session.Token;
            return true;
        }
    }

    public void Dispose() => Stop();
}

