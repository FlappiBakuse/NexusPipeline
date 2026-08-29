using NexusPipeline.App.Commands;
using NexusPipeline.App.Abstractions;
using NexusPipeline.App.Repositories;
using Microsoft.Extensions.DependencyInjection;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Plugins;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using NexusPipeline.Services.Notification;
using NexusPipeline.Services.Networking;
using NexusPipeline.Services.Update;

namespace NexusPipeline;

/// <summary>组合根：持有全局 ServiceProvider（壳式 DI）与共享数据；外部访问方式不变。服务装配见构造。持久化见 <see cref="DataStore"/>。</summary>
internal class RuntimeContext
{
    public static RuntimeContext Instance { get; } = new();

    private readonly ServiceProvider _services;

    private RuntimeContext()
    {
        ServiceCollection collection = new();
        collection.AddSingleton(new HistoryService());
        collection.AddSingleton<IScriptRepository>(_ => new RuntimeScriptRepository(FindScript, SnapshotScripts));
        collection.AddSingleton<IQueueRepository>(_ => new RuntimeQueueRepository(FindQueue, SnapshotQueues));
        collection.AddSingleton<IExecutionSnapshotProvider>(_ => new RuntimeExecutionSnapshotProvider(
            SnapshotScriptForExecution,
            SnapshotQueueForExecution));
        collection.AddSingleton<IUserRepository>(_ => new RuntimeUserRepository(action =>
        {
            lock (DataLock)
            {
                action();
            }
        }, SnapshotUsers));
        collection.AddSingleton<IUserRunDaysWriter>(_ => new RuntimeUserRunDaysWriter(
            action =>
            {
                lock (DataLock)
                {
                    action();
                }
            },
            () => Users,
            users => DataStore.SaveUsers(users)));
        collection.AddSingleton<ISettingsProvider>(_ => new RuntimeSettingsProvider(() => Settings));
        collection.AddSingleton<OutboundHttpClientProvider>(_ => new OutboundHttpClientProvider(() => Settings));
        collection.AddSingleton<AppearanceService>(provider => new AppearanceService(
            () => provider.GetRequiredService<PluginManager>()));
        collection.AddSingleton<IHistoryStore>(provider => provider.GetRequiredService<HistoryService>());
        collection.AddSingleton<PluginManager>(provider => new PluginManager(
            () => Settings,
            () => provider.GetRequiredService<NotificationDispatcher>(),
            http: provider.GetRequiredService<OutboundHttpClientProvider>(),
            tryConfigurationMutation: mutation =>
                provider.GetRequiredService<DispatchCenter>()
                    .TryExecuteHostConfigurationMutation(mutation, out _)));
        collection.AddSingleton<PluginPackageService>(provider => new PluginPackageService(
            provider.GetRequiredService<OutboundHttpClientProvider>()));
        collection.AddSingleton<PluginRepositoryService>(provider => new PluginRepositoryService(
            () => Settings,
            () => provider.GetRequiredService<PluginManager>(),
            provider.GetRequiredService<PluginPackageService>(),
            provider.GetRequiredService<OutboundHttpClientProvider>()));
        collection.AddSingleton<IPluginCapabilityResolver>(provider => provider.GetRequiredService<PluginManager>());
        collection.AddSingleton<IPluginAvailability>(provider => provider.GetRequiredService<PluginManager>());
        collection.AddSingleton<IUserRunStartingPublisher>(provider => provider.GetRequiredService<PluginManager>());
        collection.AddSingleton<NotificationDispatcher>();
        collection.AddSingleton<INotificationService>(provider => provider.GetRequiredService<NotificationDispatcher>());
        collection.AddSingleton<ExecutionAdmissionPolicy>();
        collection.AddSingleton<ExecutionPlanBuilder>();
        collection.AddSingleton<ExecutionStateStore>();
        collection.AddSingleton<ExecutionValidator>();
        collection.AddSingleton<ExecutionPreviewService>(provider => new ExecutionPreviewService(
            () => provider.GetRequiredService<DispatchCenter>(),
            () => provider.GetRequiredService<PluginManager>()));
        collection.AddSingleton<SystemActionExecutor>();
        collection.AddSingleton<ExecutionRunner>(provider => new ExecutionRunner(
            provider.GetRequiredService<IUserRepository>(),
            provider.GetRequiredService<IHistoryStore>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<SystemActionExecutor>(),
            provider.GetRequiredService<IPluginAvailability>(),
            provider.GetRequiredService<IUserRunStartingPublisher>(),
            provider.GetRequiredService<PluginManager>()));
        collection.AddSingleton<DispatchCenter>();
        collection.AddSingleton<ExecutionCommands>(provider => new ExecutionCommands(provider.GetRequiredService<DispatchCenter>()));
        collection.AddSingleton<IExecutionService>(provider => provider.GetRequiredService<ExecutionCommands>());
        collection.AddSingleton<ISchedulerStateStore>(_ => new FileSchedulerStateStore());
        collection.AddSingleton<Scheduler>();
        collection.AddSingleton<UserDataPruner>(provider => new UserDataPruner(SnapshotUsers, provider.GetRequiredService<ExecutionStateStore>()));
        collection.AddSingleton<UpdateService>(provider => new UpdateService(
            () => Settings,
            AppPaths.AppRoot,
            () => Bootstrap.CanRequestDirectExit(out _),
            Bootstrap.TryRequestUpdateExit,
            () => Bootstrap.TryAcquireUpdateMaintenanceLease(),
            provider.GetRequiredService<OutboundHttpClientProvider>()));
        _services = collection.BuildServiceProvider();
    }

    public AppSettings Settings { get; private set; } = new();

    /// <summary>脚本/队列共享列表读写锁：Web 请求线程与调度/运行后台线程并发访问时保护枚举与修改；
    /// 修改侧一律在锁内完成「读-改-写」整段，读取侧经 <see cref="FindScript"/> / <see cref="FindQueue"/> /
    /// <see cref="SnapshotScripts"/> / <see cref="SnapshotQueues"/> 或在锁内枚举，避免「集合已修改」/越界异常。</summary>
    internal readonly object DataLock = new();

    /// <summary>设置 clone-on-write 事务锁；保存成功前不发布候选引用。</summary>
    internal readonly object SettingsMutationLock = new();

    public List<ScriptInstance> Scripts { get; private set; } = new();

    public List<DispatchQueue> Queues { get; private set; } = new();

    /// <summary>全局用户实体列表；用户/脚本绑定位于 NexusUser.Bindings。</summary>
    public List<NexusUser> Users { get; private set; } = new();

    public DispatchCenter Center => Resolve<DispatchCenter>();

    public ExecutionValidator Validator => Resolve<ExecutionValidator>();

    public HistoryService History => Resolve<HistoryService>();

    public PluginManager Plugins => Resolve<PluginManager>();

    public NotificationDispatcher Notifications => Resolve<NotificationDispatcher>();

    public ExecutionCommands Commands => Resolve<ExecutionCommands>();

    public Scheduler Scheduler => Resolve<Scheduler>();

    /// <summary>服务解析出口：按类型解析已注册服务；未注册类型抛出异常。</summary>
    public T Resolve<T>() where T : notnull
    {
        return _services.GetRequiredService<T>();
    }

    public void ReloadSettings()
    {
        lock (SettingsMutationLock)
        {
            Settings = ConfigStore.Load();
        }
    }

    internal void ReplaceSettings(AppSettings settings)
    {
        Settings = settings;
    }

    public void ReloadData()
    {
        lock (DataLock)
        {
            Scripts = DataStore.LoadScripts();
            Queues = DataStore.LoadQueues();
            Users = DataStore.LoadUsers();
        }
    }

    public NexusUser? FindUser(string id)
    {
        lock (DataLock)
        {
            return Users.FirstOrDefault(user => string.Equals(user.Id, id, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>脚本列表深拷贝快照：跨线程读取/序列化用，避免与修改并发抛「集合已修改」。</summary>
    internal List<ScriptInstance> SnapshotScripts()
    {
        lock (DataLock)
        {
            return Scripts.Select(script => script.Clone()).ToList();
        }
    }

    /// <summary>队列列表深拷贝快照：跨线程读取/序列化用。</summary>
    internal List<DispatchQueue> SnapshotQueues()
    {
        lock (DataLock)
        {
            return Queues.Select(queue => queue.Clone()).ToList();
        }
    }

    /// <summary>全局用户深拷贝快照；排序和绑定读取均基于同一份快照。</summary>
    internal List<NexusUser> SnapshotUsers()
    {
        lock (DataLock)
        {
            return Users.Select(user => user.Clone()).ToList();
        }
    }

    /// <summary>在一次 DataLock 内复制单个脚本，供执行计划建立原子输入。</summary>
    internal ExecutionScriptSnapshot? SnapshotScriptForExecution(string id)
    {
        lock (DataLock)
        {
            ScriptInstance? script = Scripts.FirstOrDefault(item => item.Id == id);
            return script is null
                ? null
                : new ExecutionScriptSnapshot(script.Clone(), Users.Select(user => user.Clone()).ToList());
        }
    }

    /// <summary>在一次 DataLock 内复制队列及全部脚本，避免计划拼出仓储中不存在的混合时刻。</summary>
    internal ExecutionQueueSnapshot? SnapshotQueueForExecution(string id)
    {
        lock (DataLock)
        {
            DispatchQueue? queue = Queues.FirstOrDefault(item => item.Id == id);
            if (queue is null)
            {
                return null;
            }
            return new ExecutionQueueSnapshot(
                queue.Clone(),
                Scripts.Select(script => script.Clone()).ToList(),
                Users.Select(user => user.Clone()).ToList());
        }
    }
}
