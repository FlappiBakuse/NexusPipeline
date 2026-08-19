using Microsoft.Extensions.DependencyInjection;
using NexusPipeline.Extensibility;
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
        collection.AddSingleton<PluginManager>(provider => new PluginManager(
            new PluginHostServices(
                () => Settings,
                ReloadSettings,
                type => provider.GetRequiredService(type))));
        collection.AddSingleton(new Scheduler());
        _services = collection.BuildServiceProvider();
    }

    public AppSettings Settings { get; private set; } = new();

    /// <summary>脚本/队列共享列表读写锁（v0.7.2+，KN-04）：Web 请求线程与调度/运行后台线程并发访问时保护枚举与修改；
    /// 修改侧一律在锁内完成「读-改-写」整段，读取侧经 <see cref="FindScript"/> / <see cref="FindQueue"/> /
    /// <see cref="SnapshotScripts"/> / <see cref="SnapshotQueues"/> 或在锁内枚举，避免「集合已修改」/越界异常。</summary>
    internal readonly object DataLock = new();

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
        lock (DataLock)
        {
            Scripts = DataStore.LoadScripts();
            Queues = DataStore.LoadQueues();
        }
    }

    public ScriptInstance? FindScript(string id)
    {
        lock (DataLock)
        {
            return Scripts.FirstOrDefault(s => s.Id == id);
        }
    }

    public DispatchQueue? FindQueue(string id)
    {
        lock (DataLock)
        {
            return Queues.FirstOrDefault(q => q.Id == id);
        }
    }

    /// <summary>脚本列表深拷贝快照（v0.7.2+，KN-04）：跨线程读取/序列化用，避免与修改并发抛「集合已修改」。</summary>
    internal List<ScriptInstance> SnapshotScripts()
    {
        lock (DataLock)
        {
            return Scripts.Select(script => script.Clone()).ToList();
        }
    }

    /// <summary>队列列表深拷贝快照（v0.7.2+，KN-04）：跨线程读取/序列化用。</summary>
    internal List<DispatchQueue> SnapshotQueues()
    {
        lock (DataLock)
        {
            return Queues.Select(queue => queue.Clone()).ToList();
        }
    }
}
