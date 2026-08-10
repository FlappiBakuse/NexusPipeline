using Microsoft.Extensions.DependencyInjection;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Plugins;
using NexusPipeline.Services;

namespace NexusPipeline;

/// <summary>组合根：持有全局 ServiceProvider（壳式 DI）与共享数据；外部访问方式不变。服务装配见构造。持久化见 <see cref="DataStore"/>。</summary>
internal class RuntimeContext
{
    public static RuntimeContext Instance { get; } = new();

    private readonly ServiceProvider _services;

    private RuntimeContext()
    {
        ServiceCollection collection = new();
        collection.AddSingleton(new DispatchCenter());
        collection.AddSingleton(new HistoryService());
        collection.AddSingleton(new PluginManager());
        collection.AddSingleton(new Scheduler());
        _services = collection.BuildServiceProvider();
    }

    public AppSettings Settings { get; private set; } = new();

    public List<ScriptInstance> Scripts { get; private set; } = new();

    public List<DispatchQueue> Queues { get; private set; } = new();

    public DispatchCenter Center => Resolve<DispatchCenter>();

    public HistoryService History => Resolve<HistoryService>();

    public PluginManager Plugins => Resolve<PluginManager>();

    public Scheduler Scheduler => Resolve<Scheduler>();

    /// <summary>服务解析出口：按类型解析已注册服务；未注册类型抛出异常。</summary>
    public T Resolve<T>() where T : notnull
    {
        return _services.GetRequiredService<T>();
    }

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
