using Microsoft.Extensions.DependencyInjection;
using NexusPipeline.ControlPlane.Mcp;
using NexusPipeline.ControlPlane.Http.Services;
using NexusPipeline.Host.Composition.Adapters;
using NexusPipeline.Host.Initialization;
using NexusPipeline.Host.Lifecycle;
using NexusPipeline.Host;
using NexusPipeline.Host.State;
using NexusPipeline.Modules.Diagnostics;
using NexusPipeline.Modules.Diagnostics.Contracts;
using NexusPipeline.Modules.Configuration.Contracts;
using NexusPipeline.Modules.Configuration.Editing;
using NexusPipeline.Modules.Configuration.Validation;
using NexusPipeline.Modules.Execution.Contracts;
using NexusPipeline.Modules.Execution.Realtime;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.History.Contracts;
using NexusPipeline.Modules.History;
using NexusPipeline.Modules.Notifications.Contracts;
using NexusPipeline.Modules.Notifications;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Plugins.Repository;
using NexusPipeline.Modules.Plugins.Runtime;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Modules.Queues.Contracts;
using NexusPipeline.Modules.Queues.Queries;
using NexusPipeline.Modules.Queues.UseCases;
using NexusPipeline.Modules.Queues;
using NexusPipeline.Modules.Scheduling;
using NexusPipeline.Modules.Scheduling.Contracts;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts.Queries;
using NexusPipeline.Modules.Scripts.Resolution;
using NexusPipeline.Modules.Scripts.UseCases;
using NexusPipeline.Modules.Scripts.Validation;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Settings.Contracts;
using NexusPipeline.Modules.Settings.Persistence;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Modules.Settings.UseCases;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Modules.Users.Queries;
using NexusPipeline.Modules.Users;
using NexusPipeline.Modules.Users.UseCases;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Windows;
using NexusPipeline.Modules.Queues.Persistence;
using NexusPipeline.Modules.Scripts.Persistence;
using NexusPipeline.Modules.Users.Persistence;
using NexusPipeline.Shared.Common;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.Host.Composition;

/// <summary>组合根：只负责一次性组装 Host 对象图；进程生命周期由 <see cref="HostRuntime"/> 持有。</summary>
internal class HostCompositionRoot
{
    private readonly ServiceProvider _services;
    private readonly AutomationDefinitionState _entityState = new();
    private readonly SettingsState _settingsState;
    private readonly HostAdmissionBridge _admissionBridge;
    private readonly HostLifecycleBridge _lifecycle;

    public static HostRuntime Create(AppSettings initialSettings)
    {
        ArgumentNullException.ThrowIfNull(initialSettings);
        var lifecycle = new HostLifecycleBridge();
        var composition = new HostCompositionRoot(initialSettings, lifecycle);
        var runtime = new HostRuntime(composition);
        var bootstrap = new Bootstrap(
            runtime,
            composition.Get<PluginAutoUpdateService>(),
            composition.Get<UpdateAutomationService>(),
            StartupPipeline.TryRequestServiceExit,
            StartupPipeline.TryRequestWebOnlyExit);
        runtime.BindBootstrap(bootstrap);
        lifecycle.BindOnce(bootstrap);
        return runtime;
    }

    private HostCompositionRoot(AppSettings initialSettings, HostLifecycleBridge lifecycle)
    {
        _settingsState = new(initialSettings.Clone());
        _lifecycle = lifecycle;
        _admissionBridge = new HostAdmissionBridge(_settingsState);
        ServiceCollection collection = new();
        collection.AddSingleton(_entityState);
        collection.AddSingleton(_settingsState);
        collection.AddSingleton(_admissionBridge);
        collection.AddSingleton<ISettingsMutationGate>(_admissionBridge);
        collection.AddSingleton<IPluginConfigurationMutationGate>(_admissionBridge);
        collection.AddSingleton<IScriptMutationAdmission>(_admissionBridge);
        collection.AddSingleton<IScriptConfigGate, ScriptConfigGateAdapter>();
        collection.AddSingleton<IQueueMutationAdmission>(_admissionBridge);
        collection.AddSingleton(new RunHistoryService());
        collection.AddSingleton<NativePathPickerService>();
        collection.AddSingleton<ExecutableIconReader>();
        collection.AddSingleton<ScriptIconService>(provider => new ScriptIconService(
            provider.GetRequiredService<ScriptQueries>(),
            provider.GetRequiredService<ExecutableIconReader>()));
        collection.AddSingleton<FileBrowser>();
        collection.AddSingleton<ScriptFileBrowser>(provider => new ScriptFileBrowser(
            provider.GetRequiredService<ScriptQueries>(),
            provider.GetRequiredService<FileBrowser>()));
        collection.AddSingleton<INativePathPicker>(provider => new NativePathPickerAdapter(
            provider.GetRequiredService<NativePathPickerService>()));
        collection.AddSingleton<IScriptRepository>(_ => new RuntimeScriptRepository(_entityState));
        collection.AddSingleton<IQueueRepository>(_ => new RuntimeQueueRepository(_entityState));
        collection.AddSingleton<IExecutionSnapshotProvider>(_ => new RuntimeExecutionSnapshotProvider(_entityState));
        collection.AddSingleton<IUserRepository>(_ => new RuntimeUserRepository(_entityState));
        collection.AddSingleton<IUserSnapshotReader>(_ => new RuntimeUserSnapshotReader(_entityState));
        collection.AddSingleton<IUserRunDaysMaintenance>(new RuntimeUserRunDaysWriter(
            _entityState,
            users => UserDefinitionStore.SaveUsers(users)));
        collection.AddSingleton<IScriptMutationState>(_ => new RuntimeScriptMutationState(_entityState));
        collection.AddSingleton<IQueueMutationState>(_ => new RuntimeQueueMutationState(_entityState));
        collection.AddSingleton<IUserMutationState>(_ => new RuntimeUserMutationState(_entityState));
        collection.AddSingleton<IUserConfigDataMaintenance>(provider => new UserConfigDataMaintenance(
            provider.GetRequiredService<PluginManager>()));
        collection.AddSingleton<UserAssetService>();
        collection.AddSingleton<IQueueDataMaintenance>(provider => new QueueDataMaintenance(
            provider.GetRequiredService<PluginManager>()));
        collection.AddSingleton<IQueueScheduleSnapshotReader>(provider => new RuntimeQueueScheduleSnapshotReader(
            provider.GetRequiredService<IQueueRepository>()));
        collection.AddSingleton<IQueueUserParticipationReader>(_ => new RuntimeQueueUserParticipationReader(_entityState));
        collection.AddSingleton<ISettingsProvider>(_settingsState);
        collection.AddSingleton<IControlPlaneStatusReader, ControlPlaneStatusAdapter>();
        collection.AddSingleton<OutboundHttpClientProvider>(_ => new OutboundHttpClientProvider(
            () => OutboundProxyOptionsAdapter.FromSettings(Settings)));
        collection.AddSingleton(HostVersionInfo.Current);
        collection.AddSingleton<IHistoryStore>(provider => provider.GetRequiredService<RunHistoryService>());
        collection.AddSingleton<PluginManager>(provider => new PluginManager(
            provider.GetRequiredService<ISettingsProvider>(),
            provider.GetRequiredService<IPluginNotificationSink>(),
            provider.GetRequiredService<IPluginConfigurationMutationGate>(),
            http: provider.GetRequiredService<OutboundHttpClientProvider>(),
            hostVersion: provider.GetRequiredService<HostVersionInfo>()));
        collection.AddSingleton<PluginPackageService>(provider => new PluginPackageService(
            provider.GetRequiredService<OutboundHttpClientProvider>(),
            provider.GetRequiredService<HostVersionInfo>()));
        collection.AddSingleton<PluginRepositoryService>(provider => new PluginRepositoryService(
            provider.GetRequiredService<ISettingsProvider>(),
            provider.GetRequiredService<PluginManager>(),
            provider.GetRequiredService<PluginPackageService>(),
            provider.GetRequiredService<OutboundHttpClientProvider>(),
            provider.GetRequiredService<HostVersionInfo>()));
        collection.AddSingleton<PluginUserGlobalSettingsService>(provider => new PluginUserGlobalSettingsService(
            provider.GetRequiredService<PluginManager>()));
        collection.AddSingleton<IPluginCapabilityResolver>(provider => provider.GetRequiredService<PluginManager>());
        collection.AddSingleton<IEmulatorSupportProviderResolver>(provider => provider.GetRequiredService<PluginManager>());
        collection.AddSingleton<IPluginAvailability>(provider => provider.GetRequiredService<PluginManager>());
        collection.AddSingleton<ScriptSpecResolver>();
        collection.AddSingleton<ConfigDiagnosticFeedback>();
        collection.AddSingleton<ITaskProtocolConfigAssessmentPort, TaskProtocolConfigAssessmentAdapter>();
        collection.AddSingleton<ScriptSaveValidation>(provider => new ScriptSaveValidation(
            provider.GetRequiredService<ScriptSpecResolver>(),
            provider.GetRequiredService<IUserSnapshotReader>(),
            provider.GetRequiredService<ITaskProtocolConfigAssessmentPort>()));
        collection.AddSingleton<ScriptQueries>();
        collection.AddSingleton<QueueQueries>();
        collection.AddSingleton<UserQueries>();
        collection.AddSingleton<IUserRunStartingPublisher>(provider => provider.GetRequiredService<PluginManager>());
        collection.AddSingleton<NotificationDispatcher>();
        collection.AddSingleton<IPluginNotificationSink>(provider => new PluginNotificationSinkAdapter(
            provider.GetRequiredService<NotificationDispatcher>()));
        collection.AddSingleton<INotificationService>(provider => provider.GetRequiredService<NotificationDispatcher>());
        collection.AddSingleton<ExecutionAdmissionPolicy>();
        collection.AddSingleton<ExecutionPlanBuilder>();
        collection.AddSingleton<ExecutionStateStore>();
        collection.AddSingleton<RealtimeEventBus>();
        collection.AddSingleton<ExecutionValidator>();
        collection.AddSingleton<ExecutionPreviewService>(provider => new ExecutionPreviewService(
            provider.GetRequiredService<ExecutionDispatcher>(),
            provider.GetRequiredService<PluginManager>()));
        collection.AddSingleton<SystemActionExecutor>(provider => new SystemActionExecutor(
            provider.GetRequiredService<ExecutionStateStore>(),
            provider.GetRequiredService<RealtimeEventBus>(),
            new ApplicationExitRequestAdapter(_lifecycle.TryRequestCompletionExit)));
        collection.AddSingleton<ExecutionRunner>(provider => new ExecutionRunner(
            provider.GetRequiredService<IUserRepository>(),
            provider.GetRequiredService<IHistoryStore>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<SystemActionExecutor>(),
            provider.GetRequiredService<IPluginAvailability>(),
            provider.GetRequiredService<IEmulatorSupportProviderResolver>(),
            provider.GetRequiredService<IUserRunStartingPublisher>(),
            provider.GetRequiredService<PluginManager>(),
            provider.GetRequiredService<OutboundHttpClientProvider>()));
        collection.AddSingleton<ExecutionDispatcher>();
        collection.AddSingleton<IConfigEditAdmission>(provider => provider.GetRequiredService<ExecutionDispatcher>());
        collection.AddSingleton<IExecutionService>(provider => provider.GetRequiredService<ExecutionDispatcher>());
        collection.AddSingleton<IFrozenQueueExecutionService>(provider => provider.GetRequiredService<ExecutionDispatcher>());
        collection.AddSingleton<IAdmissionCoordination>(provider => provider.GetRequiredService<ExecutionDispatcher>());
        collection.AddSingleton<ISchedulerStateStore>(_ => new FileSchedulerStateStore());
        collection.AddSingleton<Scheduler>(provider => new Scheduler(
            provider.GetRequiredService<IQueueRepository>(),
            provider.GetRequiredService<IHistoryStore>(),
            provider.GetRequiredService<ISettingsProvider>(),
            provider.GetRequiredService<IExecutionService>(),
            provider.GetRequiredService<ExecutionValidator>(),
            provider.GetRequiredService<ExecutionPlanBuilder>(),
            provider.GetRequiredService<ISchedulerStateStore>(),
            provider.GetRequiredService<IUserRunDaysMaintenance>(),
            provider.GetRequiredService<IAdmissionCoordination>()));
        collection.AddSingleton<ISchedulerIdleReader>(provider => provider.GetRequiredService<Scheduler>());
        collection.AddSingleton<IQueueScheduleProjection>(provider => provider.GetRequiredService<Scheduler>());
        collection.AddSingleton<IUserScheduleProjection>(provider => provider.GetRequiredService<Scheduler>());
        collection.AddSingleton<RuntimePlansChanged>(provider => new RuntimePlansChanged(
            provider.GetRequiredService<Scheduler>()));
        collection.AddSingleton<IScriptPlansChanged>(provider => provider.GetRequiredService<RuntimePlansChanged>());
        collection.AddSingleton<IQueuePlansChanged>(provider => provider.GetRequiredService<RuntimePlansChanged>());
        collection.AddSingleton<IUserPlansChanged>(provider => provider.GetRequiredService<RuntimePlansChanged>());
        collection.AddSingleton<IUserMutationAdmission>(provider => new UserMutationAdmission(
            provider.GetRequiredService<ExecutionDispatcher>()));
        collection.AddSingleton<IUserMutationPolicy>(provider => new UserMutationPolicy(
            provider.GetRequiredService<ExecutionDispatcher>(),
            provider.GetRequiredService<Scheduler>(),
            provider.GetRequiredService<IPluginAvailability>()));
        collection.AddSingleton<IUserDeletionTransaction>(provider => new UserDeletionTransaction(
            provider.GetRequiredService<IUserMutationState>(),
            provider.GetRequiredService<IUserMutationAdmission>(),
            provider.GetRequiredService<IUserMutationPolicy>(),
            provider.GetRequiredService<IUserConfigDataMaintenance>(),
            provider.GetRequiredService<UserAssetService>(),
            provider.GetRequiredService<IUserPlansChanged>()));
        collection.AddSingleton<QueueCommands>(provider => new QueueCommands(
            provider.GetRequiredService<IQueueRepository>(),
            provider.GetRequiredService<IQueueMutationState>(),
            provider.GetRequiredService<IQueueScheduleSnapshotReader>(),
            provider.GetRequiredService<IQueuePlansChanged>(),
            provider.GetRequiredService<IQueueMutationAdmission>(),
            provider.GetRequiredService<IScriptRepository>(),
            provider.GetRequiredService<IQueueUserParticipationReader>(),
            provider.GetRequiredService<IPluginAvailability>(),
            provider.GetRequiredService<IQueueDataMaintenance>()));
        collection.AddSingleton<IScriptDeletionTransaction>(provider => new AutomationDefinitionTransactions(
            _entityState,
            provider.GetRequiredService<IScriptMutationAdmission>(),
            provider.GetRequiredService<PluginManager>(),
            provider.GetRequiredService<IScriptPlansChanged>()));
        collection.AddSingleton<ScriptCommands>(provider => new ScriptCommands(
            provider.GetRequiredService<IScriptRepository>(),
            provider.GetRequiredService<IScriptMutationState>(),
            provider.GetRequiredService<IScriptDeletionTransaction>(),
            provider.GetRequiredService<IScriptMutationAdmission>(),
            provider.GetRequiredService<IScriptConfigGate>(),
            provider.GetRequiredService<IPluginAvailability>(),
            provider.GetRequiredService<IPluginCapabilityResolver>(),
            provider.GetRequiredService<ScriptSpecResolver>(),
            provider.GetRequiredService<IScriptPlansChanged>()));
        collection.AddSingleton<UserCommands>(provider => new UserCommands(
            provider.GetRequiredService<IUserMutationState>(),
            provider.GetRequiredService<IUserMutationAdmission>(),
            provider.GetRequiredService<IUserMutationPolicy>(),
            provider.GetRequiredService<IUserPlansChanged>(),
            provider.GetRequiredService<IScriptRepository>(),
            provider.GetRequiredService<IScriptConfigGate>(),
            provider.GetRequiredService<IUserConfigDataMaintenance>(),
            provider.GetRequiredService<UserAssetService>(),
            provider.GetRequiredService<IUserDeletionTransaction>()));
        collection.AddSingleton<ConfigEditCommands>(provider => new ConfigEditCommands(
            provider.GetRequiredService<IConfigEditAdmission>(),
            provider.GetRequiredService<IScriptRepository>(),
            provider.GetRequiredService<IUserRepository>(),
            provider.GetRequiredService<IPluginAvailability>(),
            provider.GetRequiredService<IPluginCapabilityResolver>(),
            provider.GetRequiredService<ScriptSpecResolver>(),
            provider.GetRequiredService<UserCommands>(),
            provider.GetRequiredService<ITaskProtocolConfigAssessmentPort>()));
        collection.AddSingleton<UpdateService>(provider => new UpdateService(
            () => Settings,
            AppPaths.AppRoot,
            () => _lifecycle.CanRequestDirectExit(out _),
            _lifecycle.TryRequestUpdateExit,
            _lifecycle.TryAcquireUpdateMaintenanceLease,
            provider.GetRequiredService<OutboundHttpClientProvider>(),
            () => ApplicationHost.IsWebOnly));
        collection.AddSingleton<AutoUpdateIdlePolicy>(provider => new AutoUpdateIdlePolicy(
            provider.GetRequiredService<ExecutionDispatcher>(),
            provider.GetRequiredService<ISchedulerIdleReader>()));
        collection.AddSingleton<UpdateAutomationService>(provider => new UpdateAutomationService(
            () => Settings,
            provider.GetRequiredService<UpdateService>(),
            provider.GetRequiredService<AutoUpdateIdlePolicy>()));
        collection.AddSingleton<PluginAutoUpdateService>(provider => new PluginAutoUpdateService(
            () => Settings,
            provider.GetRequiredService<PluginRepositoryService>(),
            provider.GetRequiredService<AutoUpdateIdlePolicy>().TryAcquire,
            lease => _lifecycle.RequestRestartWithLease(lease, Audit.System)));
        collection.AddSingleton<ISettingsChangedEffects>(provider => new SettingsChangedEffects(
            provider.GetRequiredService<UpdateAutomationService>(),
            _lifecycle));
        collection.AddSingleton<IHostRestartPort>(_ => new HostRestartPortAdapter(_lifecycle));
        collection.AddSingleton<IAccessTokenPort, AccessTokenPortAdapter>();
        collection.AddSingleton<SettingsCommands>(provider => new SettingsCommands(
            _settingsState,
            provider.GetRequiredService<ISettingsMutationGate>(),
            provider.GetRequiredService<ISettingsChangedEffects>()));
        collection.AddSingleton<NexusPipeline.Modules.Users.Contracts.ITaskQueryProjection, TaskQueryProjection>();
        collection.AddSingleton<HttpRouteBindings>(provider => new HttpRouteBindings(
            provider.GetRequiredService<SettingsCommands>(),
            provider.GetRequiredService<ScriptCommands>(),
            provider.GetRequiredService<QueueCommands>(),
            provider.GetRequiredService<UserCommands>(),
            provider.GetRequiredService<ConfigEditCommands>(),
            provider.GetRequiredService<ScriptQueries>(),
            provider.GetRequiredService<ScriptSaveValidation>(),
            provider.GetRequiredService<DiagnosticsService>(),
            provider.GetRequiredService<ExecutionDispatcher>(),
            provider.GetRequiredService<ExecutionExplainService>(),
            provider.GetRequiredService<RealtimeEventBus>(),
            provider.GetRequiredService<ExecutionPreviewService>(),
            provider.GetRequiredService<QueueQueries>(),
            provider.GetRequiredService<UserQueries>(),
            provider.GetRequiredService<RunHistoryService>(),
            provider.GetRequiredService<PluginManager>(),
            provider.GetRequiredService<PluginRepositoryService>(),
            provider.GetRequiredService<PluginUserGlobalSettingsService>(),
            provider.GetRequiredService<Scheduler>(),
            provider.GetRequiredService<ISettingsProvider>(),
            provider.GetRequiredService<UpdateService>(),
            provider.GetRequiredService<UpdateAutomationService>(),
            provider.GetRequiredService<IHostRestartPort>(),
            provider.GetRequiredService<IAccessTokenPort>(),
            provider.GetRequiredService<INativePathPicker>(),
            provider.GetRequiredService<UserAssetService>(),
            provider.GetRequiredService<OutboundHttpClientProvider>(),
            provider.GetRequiredService<ScriptIconService>(),
            provider.GetRequiredService<ScriptFileBrowser>(),
            provider.GetRequiredService<NexusPipeline.Modules.Users.Contracts.ITaskQueryProjection>()));
        collection.AddSingleton<ExecutionExplainService>();
        collection.AddSingleton<DiagnosticsService>();
        _services = collection.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _admissionBridge.BindOnce(_services.GetRequiredService<ExecutionDispatcher>());
    }

    public AppSettings Settings => _settingsState.Current;

    /// <summary>Settings state owned by the Settings module; exposed only to Host composition.</summary>
    internal SettingsState SettingsState => _settingsState;

    /// <summary>运行时实体的唯一内存所有权与同步边界。</summary>
    internal AutomationDefinitionState EntityState => _entityState;

    /// <summary>设置 clone-on-write 事务锁；保存成功前不发布候选引用。</summary>
    internal object SettingsMutationLock => _settingsState.MutationLock;

    public ExecutionDispatcher Center => Resolve<ExecutionDispatcher>();

    public ExecutionValidator Validator => Resolve<ExecutionValidator>();

    public RunHistoryService History => Resolve<RunHistoryService>();

    public PluginManager Plugins => Resolve<PluginManager>();

    public NotificationDispatcher Notifications => Resolve<NotificationDispatcher>();

    public Scheduler Scheduler => Resolve<Scheduler>();

    internal UpdateService UpdateService => Resolve<UpdateService>();

    internal UpdateAutomationService UpdateAutomation => Resolve<UpdateAutomationService>();

    internal UserCommands UserCommands => Resolve<UserCommands>();

    internal HttpRouteBindings HttpRoutes => Resolve<HttpRouteBindings>();

    /// <summary>由 Host 组合根一次性组装 MCP 的显式只读/命令依赖集合。</summary>
    internal McpToolContext CreateMcpToolContext(Func<bool>? requestRestart)
    {
        return new McpToolContext(
            Resolve<ISettingsProvider>(),
            Scheduler,
            Center,
            History,
            Plugins,
            Resolve<PluginRepositoryService>(),
            Resolve<ScriptQueries>(),
            Resolve<QueueQueries>(),
            Resolve<UserQueries>(),
            Resolve<ScriptCommands>(),
            Resolve<UserCommands>(),
            Resolve<PluginUserGlobalSettingsService>(),
            Resolve<DiagnosticsService>(),
            Resolve<UpdateService>(),
            Resolve<UpdateAutomationService>(),
            Resolve<ExecutionExplainService>(),
            Resolve<HostVersionInfo>(),
            requestRestart);
    }

    /// <summary>服务解析出口：按类型解析已注册服务；未注册类型抛出异常。</summary>
    internal T Resolve<T>() where T : notnull
    {
        return _services.GetRequiredService<T>();
    }

    internal T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    internal ValueTask DisposeAsync() => _services.DisposeAsync();

    public void ReloadSettings(ConfigLoadMode mode = ConfigLoadMode.Repair)
    {
        lock (SettingsMutationLock)
        {
            _settingsState.ReplaceAfterSave(AppSettingsStore.Load(mode));
        }
    }

    internal void ReplaceSettings(AppSettings settings)
    {
        _settingsState.ReplaceAfterSave(settings);
    }

    /// <summary>只加载并发布实体内存状态；修复、规范化和落盘由 HostedRuntimeInitializer 编排。</summary>
    public void ReloadData()
    {
        List<ScriptInstance> scripts = ScriptDefinitionStore.LoadScripts(out bool scriptsAuthoritative);
        List<DispatchQueue> queues = QueueDefinitionStore.LoadQueues();
        List<NexusUser> users = UserDefinitionStore.LoadUsers();
        _entityState.ReplaceLoadedState(scripts, queues, users, scriptsAuthoritative);
    }

}
