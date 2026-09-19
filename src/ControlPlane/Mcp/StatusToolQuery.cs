using NexusPipeline.ControlPlane.Contracts;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Plugins.Runtime;
using NexusPipeline.Modules.Scheduling;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Scripts.Queries;
using NexusPipeline.Modules.Queues;
using NexusPipeline.Modules.Queues.Queries;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Modules.Settings.Contracts;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Shared.Common;
using NexusPipeline.Shared.Localization;

namespace NexusPipeline.ControlPlane.Mcp;

/// <summary>MCP 状态聚合查询；只组织现有只读服务，不持久化状态。</summary>
internal sealed class StatusToolQuery
{
    private readonly ISettingsProvider _settings;
    private readonly Scheduler _scheduler;
    private readonly ExecutionDispatcher _execution;
    private readonly ScriptQueries _scripts;
    private readonly QueueQueries _queues;
    private readonly PluginManager _plugins;
    private readonly UpdateService _updates;
    private readonly UpdateAutomationService _automation;
    private readonly HostVersionInfo _hostVersion;

    public StatusToolQuery(
        ISettingsProvider settings,
        Scheduler scheduler,
        ExecutionDispatcher execution,
        ScriptQueries scripts,
        QueueQueries queues,
        PluginManager plugins,
        UpdateService updates,
        UpdateAutomationService automation,
        HostVersionInfo hostVersion)
    {
        _settings = settings;
        _scheduler = scheduler;
        _execution = execution;
        _scripts = scripts;
        _queues = queues;
        _plugins = plugins;
        _updates = updates;
        _automation = automation;
        _hostVersion = hostVersion;
    }

    public object BuildStatus()
    {
        AppSettings settings = _settings.Current;
        (string QueueName, DateTime TriggerTime)? next = _scheduler.NextTrigger();
        PendingSystemAction? pending = _execution.CurrentSystemAction;
        IReadOnlyList<ScriptInstance> scripts = _scripts.ListEffective();
        IReadOnlyList<DispatchQueue> queues = _queues.List().Select(item => item.Queue).ToList();
        return new
        {
            service = ControlApiContract.ServiceName,
            controlApiVersion = ControlApiContract.Version,
            time = DateTime.Now,
            version = _hostVersion.CurrentVersion,
            lightweightMode = settings.LightweightMode,
            webPort = settings.WebPort,
            mcpEnabled = settings.McpEnabled,
            mcpPort = settings.McpPort,
            mcpEndpoint = IsRunningEndpoint(settings.McpPort),
            scriptCount = scripts.Count,
            queueCount = queues.Count,
            enabledQueues = queues.Count(item => item.NotifyEnabled),
            nextSchedule = next is null ? null : new { queueName = next.Value.QueueName, time = next.Value.TriggerTime },
            systemAction = pending is null ? null : new
            {
                pending.Action,
                pending.QueueName,
                pending.Deadline,
            },
            running = _execution.Active.Select(item => McpRunView.From(item.Snapshot(), includeRecords: false)).ToList(),
            plugins = _plugins.GetLocalizedPluginManagementViews(LocaleContext.Current),
        };
    }

    public object BuildUpdateStatus()
    {
        UpdateStatusSnapshot status = _updates.GetStatus();
        UpdateAutomationSnapshot automation = _automation.GetSnapshot();
        return new
        {
            state = status.State.ToString().ToLowerInvariant(),
            status.Current,
            status.Latest,
            status.Channel,
            status.Available,
            prerelease = status.LatestPrerelease == true,
            status.Notes,
            status.Progress,
            status.BytesRead,
            status.BytesTotal,
            status.Error,
            status.PolicyVerified,
            status.CanDownload,
            status.ManualUpdateRequired,
            status.UpdateBlockCode,
            status.BarrierVersion,
            status.MigrationUrl,
            status.PolicyError,
            automation = new
            {
                checkEnabled = automation.CheckEnabled,
                autoUpdateEnabled = automation.AutoUpdateEnabled,
                lastCheckAt = automation.LastAutomaticCheckAt?.ToString("O"),
                nextCheckAt = automation.NextAutomaticCheckAt?.ToString("O"),
                waitingForIdle = automation.WaitingForIdle,
                idleBlockCode = automation.IdleBlockCode,
                idleBlockReason = automation.IdleBlockReason,
            },
        };
    }

    private static string? IsRunningEndpoint(int port)
    {
        return McpHost.Current?.Port == port && McpHost.Current.IsRunning
            ? $"http://127.0.0.1:{port}/mcp"
            : null;
    }
}
