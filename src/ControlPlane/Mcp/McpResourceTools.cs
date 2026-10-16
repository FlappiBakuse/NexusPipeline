using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NexusPipeline.Modules.History;
using NexusPipeline.Modules.Queues;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Shared.Localization;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Text;

namespace NexusPipeline.ControlPlane.Mcp;

internal sealed partial class McpReadOnlyTools
{
    [McpServerTool(Name = "get_script", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("按稳定 ID 或唯一名称读取脚本、专项输入、Provider 身份和绑定。")]
    public CallToolResult GetScript(string reference)
    {
        var result = _context.ResolveScript(reference);
        return McpToolResult.From(result, value => value is null ? null : McpViews.Script(value, _context.Users));
    }

    [McpServerTool(Name = "get_user", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("按稳定 ID 或唯一名称读取用户及有效绑定设置。")]
    public CallToolResult GetUser(string reference)
    {
        var result = _context.ResolveUser(reference);
        return McpToolResult.From(result, value => value is null ? null : McpViews.User(value, _context.Queues, _context.Scheduler));
    }

    [McpServerTool(Name = "get_queue", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("按稳定 ID 或唯一名称读取队列、任务依赖、时间表和完成操作。")]
    public CallToolResult GetQueue(string reference)
    {
        var result = _context.ResolveQueue(reference);
        return McpToolResult.From(result, value => value is null ? null : McpViews.Queue(value, _context.Scheduler));
    }

    [McpServerTool(Name = "probe_script", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("只读探测专项脚本路径和声明式输入表单，不创建实例或启动脚本。")]
    public CallToolResult ProbeScript(string pluginType, string rootPath, Dictionary<string, string>? inputs = null) =>
        McpToolResult.From(_context.ScriptCommands.Probe(pluginType, rootPath, inputs));

    [McpServerTool(Name = "get_task_summaries", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("读取用户绑定的最近任务结果及运行准入摘要。")]
    public CallToolResult GetTaskSummaries()
    {
        try { return McpToolResult.Success(_context.TaskQueries.Summaries()); }
        catch (TaskQueryUnavailableException) { return McpToolResult.Failure("unsupported_history_index", "历史索引格式不支持"); }
    }

    [McpServerTool(Name = "get_task_plan", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("只读发现指定用户绑定的任务计划和配置诊断，不修改快照或运行目标软件。")]
    public async Task<CallToolResult> GetTaskPlan(string userReference, string scriptReference, CancellationToken cancellationToken = default)
    {
        var user = _context.ResolveUser(userReference);
        if (!user.Succeeded) return McpToolResult.From(user);
        var script = _context.ResolveScript(scriptReference);
        if (!script.Succeeded) return McpToolResult.From(script);
        return McpToolResult.Success(await _context.TaskQueries.PreviewAsync(user.Value!.Id, script.Value!.Id, cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(Name = "preview_config_repair", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("读取受支持配置修复建议；不提交修复。提交由 CLI 或本机管理页面完成。")]
    public CallToolResult PreviewConfigRepair(string userReference, string scriptReference)
    {
        var user = _context.ResolveUser(userReference);
        if (!user.Succeeded) return McpToolResult.From(user);
        var script = _context.ResolveScript(scriptReference);
        if (!script.Succeeded) return McpToolResult.From(script);
        return McpToolResult.From(_context.ConfigEdits.PreviewRepair(script.Value!.Id, user.Value!.Id));
    }

    [McpServerTool(Name = "get_run_tasks", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("读取活动或最近结束运行的专项任务报告。")]
    public CallToolResult GetRunTasks(string runId)
    {
        var run = _context.Center.FindAny(runId);
        return run is null ? McpToolResult.Failure("not_found", "运行不存在") : McpToolResult.Success(run.SnapshotTasks());
    }

    [McpServerTool(Name = "get_history", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("读取历史记录、任务结果和每次尝试最多 200 行日志尾部。")]
    public CallToolResult GetHistory(string recordId)
    {
        var record = _context.History.FindById(recordId)?.Clone();
        if (record is null) return McpToolResult.Failure("not_found", "历史记录不存在");
        _context.Plugins.LocalizeHistory(record, LocaleContext.Current);
        return McpToolResult.Success(new { record = RunHistoryService.ToView(record), attemptLogs = record.AttemptDetails.Select(attempt =>
        {
            var log = _context.History.ReadScriptLog(record, attempt.Number);
            return new { number = attempt.Number, logTail = log is null ? null : TextTail.TakeTail(log.Value.LogText, 200), logTotalLines = log?.TotalLines ?? 0 };
        }).ToArray() });
    }

    [McpServerTool(Name = "get_history_summary", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("按最近天数和可选稳定脚本 ID、队列 ID、用户历史键、状态统计历史。")]
    public CallToolResult GetHistorySummary(int days = 3, string? scriptId = null, string? queueId = null, string? userKey = null, string? status = null)
    {
        if (days < 1 || days > NexusPipeline.Modules.Settings.AppFixedLimits.HistoryRetentionDaysMax
            || !RunHistoryService.IsValidStatus(RunHistoryService.NormalizeStatus(status)))
            return McpToolResult.Failure("validation_error", "历史范围或状态无效");
        return McpToolResult.Success(_context.History.Summarize(DateTime.Today.AddDays(1 - days), DateTime.Now.AddMinutes(5), scriptId, queueId, userKey, RunHistoryService.NormalizeStatus(status)));
    }

    [McpServerTool(Name = "list_provider_configurations", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("读取 Provider 配置初始化操作的状态和进度，不启动或取消操作。")]
    public CallToolResult ListProviderConfigurations() => McpToolResult.Success(_context.ProviderConfigurations.Snapshot());

    [McpServerTool(Name = "get_dashboard", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("读取今日概览卡片目录、显示顺序和可用状态。")]
    public CallToolResult GetDashboard() => McpToolResult.Success(_context.Dashboard.Read(LocaleContext.Current));
}

internal sealed class McpQueueInput
{
    public string Name { get; set; } = "";
    public string AutoRunMode { get; set; } = "none";
    public List<QueueTimeSet> TimeSets { get; set; } = new();
    public List<QueueTask> Tasks { get; set; } = new();
    public bool NotifyEnabled { get; set; }
    [Description("必须为 none；MCP 不能配置系统完成操作。")]
    public string CompletionAction { get; set; } = "none";
    internal DispatchQueue ToModel() => new() { Name = Name, AutoRunMode = AutoRunMode, TimeSets = TimeSets,
        Tasks = Tasks, NotifyEnabled = NotifyEnabled, CompletionAction = CompletionAction };
}

internal sealed partial class McpMutationTools
{
    [McpServerTool(Name = "update_user", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("更新已有用户的名称和备注；复用运行锁和持久化校验。")]
    public CallToolResult UpdateUser(string reference, McpUserInput input)
    {
        if (input is null) return McpToolResult.Failure("validation_error", "用户输入不能为空");
        var user = _context.ResolveUser(reference);
        if (!user.Succeeded) return McpToolResult.From(user);
        return McpToolResult.From(_context.UserCommands.Update(user.Value!.Id, input.Name, input.Remark, Audit.Mcp),
            value => value is null ? null : McpViews.User(value, _context.Queues, _context.Scheduler));
    }

    [McpServerTool(Name = "create_queue", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("创建安全队列；任务使用脚本稳定 ID，completionAction 必须为 none。")]
    public CallToolResult CreateQueue(McpQueueInput input) => SaveSafeQueue(null, input);

    [McpServerTool(Name = "update_queue", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("更新安全队列；拒绝带系统完成操作的现有队列和输入。")]
    public CallToolResult UpdateQueue(string reference, McpQueueInput input) => string.IsNullOrWhiteSpace(reference)
        ? McpToolResult.Failure("validation_error", "队列引用不能为空") : SaveSafeQueue(reference, input);

    private CallToolResult SaveSafeQueue(string? reference, McpQueueInput input)
        => _context.Center.WithAdmissionCoordination(() => SaveSafeQueueCore(reference, input));

    private CallToolResult SaveSafeQueueCore(string? reference, McpQueueInput input)
    {
        if (input is null || input.Tasks is null || input.TimeSets is null)
            return McpToolResult.Failure("validation_error", "队列输入不能为空");
        var model = input.ToModel();
        var policy = McpPolicy.ValidateQueueExecution(model);
        if (!policy.Succeeded) return McpToolResult.From(policy);
        string? id = null;
        if (reference is not null)
        {
            var target = _context.ResolveQueue(reference);
            if (!target.Succeeded) return McpToolResult.From(target);
            var currentPolicy = McpPolicy.ValidateQueueExecution(target.Value!);
            if (!currentPolicy.Succeeded) return McpToolResult.From(currentPolicy);
            id = target.Value!.Id;
        }
        return McpToolResult.From(id is null ? _context.QueueCommands.Create(model, Audit.Mcp) : _context.QueueCommands.Update(id, model, Audit.Mcp),
            value => value is null ? null : McpViews.Queue(value, _context.Scheduler));
    }
}
