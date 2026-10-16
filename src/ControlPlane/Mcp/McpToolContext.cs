using NexusPipeline.ControlPlane.Contracts;
using NexusPipeline.Modules.Diagnostics;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.History;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Modules.Plugins.Runtime;
using NexusPipeline.Modules.Queues.Queries;
using NexusPipeline.Modules.Queues;
using NexusPipeline.Modules.Scheduling;
using NexusPipeline.Modules.Scripts.Queries;
using NexusPipeline.Modules.Scripts.UseCases;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Modules.Settings.Contracts;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Modules.Users.Queries;
using NexusPipeline.Modules.Users.UseCases;
using NexusPipeline.Modules.Users;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Shared.Localization;
using NexusPipeline.Shared.Results;
using NexusPipeline.Shared.Text;
using NexusPipeline.Shared.Common;

namespace NexusPipeline.ControlPlane.Mcp;

/// <summary>MCP 适配层唯一的组合根访问入口，隔离 Web/CLI 路由并复用应用命令与服务。</summary>
internal sealed class McpToolContext
{
    private readonly McpTargetResolver _targetResolver;
    private readonly StatusToolQuery _statusQuery;

    public McpToolContext(
        ISettingsProvider settings,
        Scheduler scheduler,
        ExecutionDispatcher center,
        RunHistoryService history,
        PluginManager plugins,
        ScriptQueries scriptQueries,
        QueueQueries queueQueries,
        UserQueries userQueries,
        ScriptCommands scriptCommands,
        UserCommands userCommands,
        DiagnosticsService diagnostics,
        UpdateService updateService,
        UpdateAutomationService updateAutomation,
        ExecutionExplainService executionExplain,
        HostVersionInfo hostVersion)
    {
        Settings = settings;
        Scheduler = scheduler;
        Center = center;
        History = history;
        Plugins = plugins;
        ScriptCommands = scriptCommands;
        UserCommands = userCommands;
        Diagnostics = diagnostics;
        ExecutionExplain = executionExplain;
        HostVersion = hostVersion;
        _targetResolver = new McpTargetResolver(scriptQueries, queueQueries, userQueries);
        _statusQuery = new StatusToolQuery(
            settings,
            scheduler,
            center,
            scriptQueries,
            queueQueries,
            plugins,
            updateService,
            updateAutomation,
            hostVersion);
    }

    public ISettingsProvider Settings { get; }

    public Scheduler Scheduler { get; }

    public ExecutionDispatcher Center { get; }

    public RunHistoryService History { get; }

    public PluginManager Plugins { get; }

    internal ScriptCommands ScriptCommands { get; }

    internal UserCommands UserCommands { get; }

    public DiagnosticsService Diagnostics { get; }

    public ExecutionExplainService ExecutionExplain { get; }

    internal HostVersionInfo HostVersion { get; }

    public IReadOnlyList<ScriptInstance> Scripts => _targetResolver.Scripts;

    public IReadOnlyList<DispatchQueue> Queues => _targetResolver.Queues;

    public IReadOnlyList<NexusUser> Users => _targetResolver.Users;

    public OperationResult<ScriptInstance> ResolveScript(string? reference) => _targetResolver.ResolveScript(reference);

    public OperationResult<DispatchQueue> ResolveQueue(string? reference) => _targetResolver.ResolveQueue(reference);

    public OperationResult<NexusUser> ResolveUser(string? reference) => _targetResolver.ResolveUser(reference);

    public object BuildStatus() => _statusQuery.BuildStatus();

    public object GetSettings() => McpViews.Settings(Settings.Current);

    public object GetDiagnostics() => Diagnostics.CreateSnapshot();

    public object GetUpdateStatus() => _statusQuery.BuildUpdateStatus();

}
