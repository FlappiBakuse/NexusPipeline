using NexusPipeline.Modules.Settings.UseCases;
using NexusPipeline.Modules.Scripts.UseCases;
using NexusPipeline.Modules.Queues.UseCases;
using NexusPipeline.Modules.Users.UseCases;
using NexusPipeline.Modules.Configuration.Editing;
using NexusPipeline.Modules.Configuration.Validation;
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
using NexusPipeline.ControlPlane.Http;

namespace NexusPipeline.ControlPlane.Http.Services;

/// <summary>
/// Explicit Host-to-control-plane route dependencies.  This is a small
/// protocol binding, not a general service resolver.
/// </summary>
internal interface IHttpRouteBindings
{
    IReadOnlyDictionary<string, ApiRouteCatalog.BoundRoute> Routes { get; }

    SettingsCommands SettingsCommands { get; }

    ScriptCommands ScriptCommands { get; }

    QueueCommands QueueCommands { get; }

    UserCommands UserCommands { get; }

    ConfigEditCommands ConfigEditCommands { get; }

    ScriptQueries ScriptQueries { get; }

    ScriptSaveValidation ScriptSaveValidation { get; }

    DiagnosticsService Diagnostics { get; }

    ExecutionDispatcher ExecutionDispatcher { get; }

    ExecutionExplainService ExecutionExplain { get; }

    RealtimeEventBus RealtimeEvents { get; }

    ExecutionPreviewService ExecutionPreview { get; }

    QueueQueries QueueQueries { get; }

    UserQueries UserQueries { get; }

    RunHistoryService History { get; }

    PluginManager Plugins { get; }

    PluginRepositoryService PluginRepository { get; }

    PluginUserGlobalSettingsService PluginUserGlobalSettings { get; }

    Scheduler Scheduler { get; }

    ISettingsProvider Settings { get; }

    UpdateService Updates { get; }

    UpdateAutomationService UpdateAutomation { get; }

    IHostRestartPort Restart { get; }

    IAccessTokenPort AccessToken { get; }

    INativePathPicker NativePathPicker { get; }

    UserAssetService UserAssets { get; }

    OutboundHttpClientProvider OutboundHttp { get; }

    ScriptIconService ScriptIcons { get; }

    ScriptFileBrowser ScriptFileBrowser { get; }
    NexusPipeline.Modules.Users.Contracts.ITaskQueryProjection TaskQueries { get; }
}
