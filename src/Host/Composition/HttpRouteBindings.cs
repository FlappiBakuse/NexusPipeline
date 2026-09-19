using NexusPipeline.ControlPlane.Http.Services;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Modules.Settings.UseCases;
using NexusPipeline.Modules.Scripts.UseCases;
using NexusPipeline.Modules.Queues.UseCases;
using NexusPipeline.Modules.Users.UseCases;
using NexusPipeline.Modules.Configuration.Editing;
using NexusPipeline.Modules.Scripts.Queries;
using NexusPipeline.Modules.Scripts.Validation;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Diagnostics;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Execution.Realtime;
using NexusPipeline.Modules.History;
using NexusPipeline.Modules.Plugins.Repository;
using NexusPipeline.Modules.Plugins.Runtime;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Modules.Queues.Queries;
using NexusPipeline.Modules.Scheduling;
using NexusPipeline.Modules.Settings.Contracts;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Modules.Users;
using NexusPipeline.Modules.Users.Queries;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Windows;

namespace NexusPipeline.Host.Composition;

/// <summary>HTTP route binding assembled by Host composition.</summary>
internal sealed class HttpRouteBindings : IHttpRouteBindings
{
    public HttpRouteBindings(
        SettingsCommands settingsCommands,
        ScriptCommands scriptCommands,
        QueueCommands queueCommands,
        UserCommands userCommands,
        ConfigEditCommands configEditCommands,
        ScriptQueries scriptQueries,
        ScriptSaveValidation scriptSaveValidation,
        DiagnosticsService diagnostics,
        ExecutionDispatcher dispatchCenter,
        ExecutionExplainService executionExplain,
        RealtimeEventBus realtimeEvents,
        ExecutionPreviewService executionPreview,
        QueueQueries queueQueries,
        UserQueries userQueries,
        RunHistoryService history,
        PluginManager plugins,
        PluginRepositoryService pluginRepository,
        PluginUserGlobalSettingsService pluginUserGlobalSettings,
        Scheduler scheduler,
        ISettingsProvider settings,
        UpdateService updates,
        UpdateAutomationService updateAutomation,
        INativePathPicker nativePathPicker,
        UserAssetService userAssets,
        OutboundHttpClientProvider outboundHttp,
        ScriptIconService scriptIcons,
        ScriptFileBrowser scriptFileBrowser)
    {
        Routes = ApiRouteCatalog.Bind(typeof(WebServer).Assembly, Array.Empty<object>());
        SettingsCommands = settingsCommands;
        ScriptCommands = scriptCommands;
        QueueCommands = queueCommands;
        UserCommands = userCommands;
        ConfigEditCommands = configEditCommands;
        ScriptQueries = scriptQueries;
        ScriptSaveValidation = scriptSaveValidation;
        Diagnostics = diagnostics;
        ExecutionDispatcher = dispatchCenter;
        ExecutionExplain = executionExplain;
        RealtimeEvents = realtimeEvents;
        ExecutionPreview = executionPreview;
        QueueQueries = queueQueries;
        UserQueries = userQueries;
        History = history;
        Plugins = plugins;
        PluginRepository = pluginRepository;
        PluginUserGlobalSettings = pluginUserGlobalSettings;
        Scheduler = scheduler;
        Settings = settings;
        Updates = updates;
        UpdateAutomation = updateAutomation;
        NativePathPicker = nativePathPicker;
        UserAssets = userAssets;
        OutboundHttp = outboundHttp;
        ScriptIcons = scriptIcons;
        ScriptFileBrowser = scriptFileBrowser;
    }

    public IReadOnlyDictionary<string, ApiRouteCatalog.BoundRoute> Routes { get; }

    public SettingsCommands SettingsCommands { get; }

    public ScriptCommands ScriptCommands { get; }

    public QueueCommands QueueCommands { get; }

    public UserCommands UserCommands { get; }

    public ConfigEditCommands ConfigEditCommands { get; }

    public ScriptQueries ScriptQueries { get; }

    public ScriptSaveValidation ScriptSaveValidation { get; }

    public DiagnosticsService Diagnostics { get; }

    public ExecutionDispatcher ExecutionDispatcher { get; }

    public ExecutionExplainService ExecutionExplain { get; }

    public RealtimeEventBus RealtimeEvents { get; }

    public ExecutionPreviewService ExecutionPreview { get; }

    public QueueQueries QueueQueries { get; }

    public UserQueries UserQueries { get; }

    public RunHistoryService History { get; }

    public PluginManager Plugins { get; }

    public PluginRepositoryService PluginRepository { get; }

    public PluginUserGlobalSettingsService PluginUserGlobalSettings { get; }

    public Scheduler Scheduler { get; }

    public ISettingsProvider Settings { get; }

    public UpdateService Updates { get; }

    public UpdateAutomationService UpdateAutomation { get; }

    public INativePathPicker NativePathPicker { get; }

    public UserAssetService UserAssets { get; }

    public OutboundHttpClientProvider OutboundHttp { get; }

    public ScriptIconService ScriptIcons { get; }

    public ScriptFileBrowser ScriptFileBrowser { get; }
}
