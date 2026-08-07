namespace NexusPipeline;

public interface IPlugin
{
    string Name { get; }

    string DisplayName { get; }

    string Description { get; }

    string Version { get; }

    bool IsBuiltIn { get; }

    void Initialize(PluginContext context);

    void Shutdown();
}

public class PluginContext
{
    public void Log(string message)
    {
        Logger.Info($"[插件] {message}");
    }

    public void ReloadSettings()
    {
        RuntimeContext.Instance.ReloadSettings();
    }
}
