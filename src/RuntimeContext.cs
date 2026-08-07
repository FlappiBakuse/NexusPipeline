using NexusPipeline.Plugins;

namespace NexusPipeline;

/// <summary>组合根：持有全部领域服务单例与共享数据。持久化见 <see cref="DataStore"/>。</summary>
internal class RuntimeContext
{
    public static RuntimeContext Instance { get; } = new();

    public AppSettings Settings { get; private set; } = new();

    public List<ScriptInstance> Scripts { get; private set; } = new();

    public List<DispatchQueue> Queues { get; private set; } = new();

    public DispatchCenter Center { get; private set; } = new();

    public HistoryService History { get; private set; } = new();

    public PluginManager Plugins { get; private set; } = new();

    public Scheduler Scheduler { get; private set; } = new();

    public void ReloadSettings()
    {
        Settings = ConfigStore.Load();
    }

    public void ReloadData()
    {
        Scripts = DataStore.LoadScripts();
        Queues = DataStore.LoadQueues();
    }

    public ScriptInstance? FindScript(string id)
    {
        return Scripts.FirstOrDefault(s => s.Id == id);
    }

    public DispatchQueue? FindQueue(string id)
    {
        return Queues.FirstOrDefault(q => q.Id == id);
    }
}
