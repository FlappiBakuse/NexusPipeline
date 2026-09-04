using NexusPipeline.App.Abstractions;
using NexusPipeline.App.Repositories;
using NexusPipeline.App.Queries;
using NexusPipeline.App.State;
using Microsoft.Extensions.DependencyInjection;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Plugins;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using NexusPipeline.Services.Notification;
using NexusPipeline.Services.Networking;
using NexusPipeline.Services.Update;
using NexusPipeline.Utilities;

namespace NexusPipeline;

/// <summary>组合根：持有全局 ServiceProvider（壳式 DI）与设置生命周期；运行时实体状态由 <see cref="RuntimeEntityState"/> 唯一持有。服务装配见构造，持久化见 <see cref="DataStore"/>。</summary>
internal class RuntimeContext
{
    public static RuntimeContext Instance { get; } = new();

    private readonly ServiceProvider _services;
    private readonly RuntimeEntityState _entityState = new();

    private RuntimeContext()
    {
        ServiceCollection collection = new();
        collection.AddSingleton(_entityState);
        collection.AddSingleton(new HistoryService());
        collection.AddSingleton<NativePathPickerService>();
        collection.AddSingleton<IScriptRepository>(_ => new RuntimeScriptRepository(_entityState));
        collection.AddSingleton<IQueueRepository>(_ => new RuntimeQueueRepository(_entityState));
        collection.AddSingleton<IExecutionSnapshotProvider>(_ => new RuntimeExecutionSnapshotProvider(_entityState));
        collection.AddSingleton<IUserRepository>(_ => new RuntimeUserRepository(_entityState));
        collection.AddSingleton<Func<bool>>(new RuntimeUserRunDaysWriter(
            _entityState,
            users => DataStore.SaveUsers(users)).DecrementDaily);
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
        collection.AddSingleton<PluginUserGlobalSettingsService>(provider => new PluginUserGlobalSettingsService(
            provider.GetRequiredService<PluginManager>()));
        collection.AddSingleton<IPluginCapabilityResolver>(provider => provider.GetRequiredService<PluginManager>());
        collection.AddSingleton<IPluginAvailability>(provider => provider.GetRequiredService<PluginManager>());
        collection.AddSingleton<ScriptSpecResolver>();
        collection.AddSingleton<ScriptQueries>();
        collection.AddSingleton<QueueQueries>();
        collection.AddSingleton<UserQueries>();
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
        collection.AddSingleton<IExecutionService>(provider => provider.GetRequiredService<DispatchCenter>());
        collection.AddSingleton<IFrozenQueueExecutionService>(provider => provider.GetRequiredService<DispatchCenter>());
        collection.AddSingleton<ISchedulerStateStore>(_ => new FileSchedulerStateStore());
        collection.AddSingleton<Scheduler>();
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

    /// <summary>运行时实体的唯一内存所有权与同步边界。</summary>
    internal RuntimeEntityState EntityState => _entityState;

    /// <summary>设置 clone-on-write 事务锁；保存成功前不发布候选引用。</summary>
    internal readonly object SettingsMutationLock = new();

    public DispatchCenter Center => Resolve<DispatchCenter>();

    public ExecutionValidator Validator => Resolve<ExecutionValidator>();

    public HistoryService History => Resolve<HistoryService>();

    public PluginManager Plugins => Resolve<PluginManager>();

    public NotificationDispatcher Notifications => Resolve<NotificationDispatcher>();

    public Scheduler Scheduler => Resolve<Scheduler>();

    /// <summary>服务解析出口：按类型解析已注册服务；未注册类型抛出异常。</summary>
    public T Resolve<T>() where T : notnull
    {
        return _services.GetRequiredService<T>();
    }

    public void ReloadSettings(ConfigLoadMode mode = ConfigLoadMode.Repair)
    {
        lock (SettingsMutationLock)
        {
            Settings = ConfigStore.Load(mode);
        }
    }

    internal void ReplaceSettings(AppSettings settings)
    {
        Settings = settings;
    }

    /// <summary>只加载并发布实体内存状态；修复、迁移和落盘由 HostedRuntimeInitializer 编排。</summary>
    public void ReloadData()
    {
        List<ScriptInstance> scripts = DataStore.LoadScripts(out bool scriptsAuthoritative);
        List<DispatchQueue> queues = DataStore.LoadQueues();
        List<NexusUser> users = DataStore.LoadUsers();
        _entityState.ReplaceLoadedState(scripts, queues, users, scriptsAuthoritative);
    }

}
