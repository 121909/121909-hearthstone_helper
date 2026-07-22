using System.Threading;
using Xunit;

namespace DiscardAdvisor.Plugin.Tests;

public sealed class PluginLifetimeTests
{
    [Fact]
    public void StartsAndStopsASession()
    {
        using var lifetime = new PluginLifetime();

        var token = lifetime.Start();

        Assert.Equal(PluginRunState.Running, lifetime.State);
        Assert.Equal(1, lifetime.Generation);
        Assert.False(token.IsCancellationRequested);

        lifetime.Stop();

        Assert.Equal(PluginRunState.Stopped, lifetime.State);
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void StartAndStopAreIdempotent()
    {
        using var lifetime = new PluginLifetime();

        var first = lifetime.Start();
        var second = lifetime.Start();

        Assert.Equal(first, second);
        Assert.Equal(1, lifetime.Generation);

        lifetime.Stop();
        lifetime.Stop();

        Assert.Equal(PluginRunState.Stopped, lifetime.State);
    }

    [Fact]
    public void RestartCreatesANewGeneration()
    {
        using var lifetime = new PluginLifetime();
        var first = lifetime.Start();
        lifetime.Stop();

        var second = lifetime.Start();

        Assert.True(first.IsCancellationRequested);
        Assert.False(second.IsCancellationRequested);
        Assert.NotEqual(first, second);
        Assert.Equal(2, lifetime.Generation);
    }

    [Fact]
    public void SessionIsUnavailableWhileStopped()
    {
        using var lifetime = new PluginLifetime();

        Assert.False(lifetime.TryGetSession(out var stoppedToken));
        Assert.Equal(CancellationToken.None, stoppedToken);

        var runningToken = lifetime.Start();

        Assert.True(lifetime.TryGetSession(out var observedToken));
        Assert.Equal(runningToken, observedToken);
    }
}

