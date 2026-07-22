namespace DiscardAdvisor.Plugin;

public interface IPluginRuntime
{
    PluginRunState State { get; }

    void Start();

    void Stop();

    void Update();
}

