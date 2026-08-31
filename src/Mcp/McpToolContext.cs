using NexusPipeline.App;
using NexusPipeline.App.Contracts;
using NexusPipeline.Models;
using NexusPipeline.Plugins;
using NexusPipeline.Services;
using NexusPipeline.Services.Update;
using NexusPipeline.Utilities;

namespace NexusPipeline.Mcp;

/// <summary>MCP 适配层唯一的组合根访问入口，隔离 Web/CLI 路由并复用应用命令与服务。</summary>
internal sealed class McpToolContext
{
    public McpToolContext(
        RuntimeContext runtime,
        Func<bool>? requestRestart)
    {
        Runtime = runtime;
        RequestRestart = requestRestart;
    }

    public RuntimeContext Runtime { get; }


    public Func<bool>? RequestRestart { get; }

    internal PluginUserGlobalSettingsService UserGlobalSettings => Runtime.Resolve<PluginUserGlobalSettingsService>();

    public IReadOnlyList<ScriptInstance> Scripts => Runtime.SnapshotScripts()
        .OrderBy(item => item.Index)
        .ToList();

    public IReadOnlyList<DispatchQueue> Queues => Runtime.SnapshotQueues()
        .OrderBy(item => item.Index)
        .ToList();

    public IReadOnlyList<NexusUser> Users => Runtime.SnapshotUsers()
        .OrderBy(item => item.Index)
        .ToList();

    public OperationResult<ScriptInstance> ResolveScript(string? reference)
    {
        TargetResolution<ScriptInstance> resolution = TargetResolver.ResolveScript(Scripts, reference);
        return Resolve(resolution, "脚本实例", item => $"{item.Name}（id={item.Id}）");
    }

    public OperationResult<DispatchQueue> ResolveQueue(string? reference)
    {
        TargetResolution<DispatchQueue> resolution = TargetResolver.ResolveQueue(Queues, reference);
        return Resolve(resolution, "调度队列", item => $"{item.Name}（id={item.Id}）");
    }

    public OperationResult<NexusUser> ResolveUser(string? reference)
    {
        TargetResolution<NexusUser> resolution = TargetResolver.ResolveUser(Users, reference);
        return Resolve(resolution, "全局用户", item => $"{item.Name}（id={item.Id}）");
    }

    public object BuildStatus()
    {
        AppSettings settings = Runtime.Settings;
        (string QueueName, DateTime TriggerTime)? next = Runtime.Scheduler.NextTrigger();
        PendingSystemAction? pending = Runtime.Center.CurrentSystemAction;
        IReadOnlyList<ScriptInstance> scripts = Scripts;
        IReadOnlyList<DispatchQueue> queues = Queues;
        return new
        {
            service = ControlApiContract.ServiceName,
            controlApiVersion = ControlApiContract.Version,
            time = DateTime.Now,
            version = UpdateService.CurrentVersion,
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
            running = Runtime.Center.Active.Select(item => McpRunView.From(item.Snapshot(), includeRecords: false)).ToList(),
            plugins = Runtime.Plugins.PluginManagementViews,
        };
    }

    public object GetSettings() => McpViews.Settings(Runtime.Settings);

    public object GetUpdateStatus()
    {
        UpdateStatusSnapshot status = Runtime.Resolve<UpdateService>().GetStatus();
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
        };
    }

    public object GetHistoryDetail(RunRecord record)
    {
        var attemptLogs = record.AttemptDetails.Select(attempt =>
        {
            (string LogText, int TotalLines)? log = Runtime.History.ReadScriptLog(record, attempt.Number);
            return new
            {
                attempt.Number,
                logTail = log is null ? null : TextRules.TakeTail(log.Value.LogText, 200),
                logTotalLines = log?.TotalLines ?? 0,
            };
        }).ToList();
        return new { record, attemptLogs };
    }

    public static OperationResult<T> NotFound<T>(string message) =>
        OperationResult<T>.Failure("not_found", message, OperationErrorKind.NotFound);

    public static OperationResult<T> Forbidden<T>(string code, string message) =>
        OperationResult<T>.Failure(code, message, OperationErrorKind.Forbidden);

    private static OperationResult<T> Resolve<T>(
        TargetResolution<T> resolution,
        string label,
        Func<T, string> describe)
    {
        return resolution.Kind switch
        {
            TargetResolutionKind.Found => OperationResult<T>.Ok(resolution.Value!),
            TargetResolutionKind.Ambiguous => OperationResult<T>.Failure(
                "ambiguous_target",
                $"{label}引用匹配到多个对象，请改用稳定 ID",
                OperationErrorKind.Conflict,
                resolution.Candidates.Select(describe).ToArray()),
            _ => OperationResult<T>.Failure(
                "not_found",
                $"未找到{label}：引用为空或对象不存在",
                OperationErrorKind.NotFound),
        };
    }

    private static string? IsRunningEndpoint(int port)
    {
        return McpHost.Current?.Port == port && McpHost.Current.IsRunning
            ? $"http://127.0.0.1:{port}/mcp"
            : null;
    }
}
