namespace NexusPipeline;

public class RuntimeContext
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
        Scripts = JsonStore.LoadList<ScriptInstance>(AppPaths.ScriptsPath);
        Queues = JsonStore.LoadList<DispatchQueue>(AppPaths.QueuesPath);
    }

    public void SaveScripts()
    {
        JsonStore.SaveList(AppPaths.ScriptsPath, Scripts);
    }

    public void SaveQueues()
    {
        JsonStore.SaveList(AppPaths.QueuesPath, Queues);
    }

    public ScriptInstance? FindScript(string id)
    {
        return Scripts.FirstOrDefault(s => s.Id == id);
    }

    public DispatchQueue? FindQueue(string id)
    {
        return Queues.FirstOrDefault(q => q.Id == id);
    }

    public string ScriptName(string id)
    {
        return FindScript(id)?.Name ?? id;
    }

    public string QueueName(string id)
    {
        return FindQueue(id)?.Name ?? id;
    }
}
