using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NexusPipeline.App.Contracts;
using NexusPipeline.Models;
using NexusPipeline.Plugins;
using NexusPipeline.Services;
using NexusPipeline.Services.Update;

namespace NexusPipeline.Mcp;

/// <summary>默认暴露的只读 MCP 工具。</summary>
[McpServerToolType]
internal sealed class McpReadOnlyTools
{
    private readonly McpToolContext _context;

    public McpReadOnlyTools(McpToolContext context)
    {
        _context = context;
    }

    [McpServerTool(Name = "get_status", Title = "获取运行状态", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("读取 NexusPipeline 当前服务、调度、运行任务、插件和 MCP 端点状态。")]
    public CallToolResult GetStatus() => McpToolResult.Success(_context.BuildStatus());

    [McpServerTool(Name = "list_scripts", Title = "列出脚本实例", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("列出所有脚本实例及其路径、执行策略和全局用户绑定。")]
    public CallToolResult ListScripts()
    {
        IReadOnlyList<NexusUser> users = _context.Users;
        return McpToolResult.Success(_context.Scripts.Select(script => McpViews.Script(script, users)).ToList());
    }

    [McpServerTool(Name = "list_users", Title = "列出全局用户", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("列出全局用户、绑定数量、绑定设置和下一次队列计划。")]
    public CallToolResult ListUsers()
    {
        IReadOnlyList<DispatchQueue> queues = _context.Queues;
        return McpToolResult.Success(_context.Users.Select(user => McpViews.User(user, queues)).ToList());
    }

    [McpServerTool(Name = "list_queues", Title = "列出调度队列", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("列出调度队列、任务顺序、时间表、通知开关和下一次触发时间。")]
    public CallToolResult ListQueues()
    {
        return McpToolResult.Success(_context.Queues.Select(McpViews.Queue).ToList());
    }

    [McpServerTool(Name = "list_runs", Title = "列出运行任务", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("列出当前活动运行任务；运行结束后可用 get_run 读取内存中的最终状态。")]
    public CallToolResult ListRuns()
    {
        return McpToolResult.Success(_context.Runtime.Center.Active
            .Select(item => McpRunView.From(item.Snapshot(), includeRecords: false))
            .ToList());
    }

    [McpServerTool(Name = "get_run", Title = "获取运行任务", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("按 runId 获取活动或最近已结束的运行任务状态、运行记录和日志尾部。")]
    public CallToolResult GetRun([Description("run_script 或 run_queue 返回的 runId。")]
        string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return McpToolResult.Failure("validation_error", "runId 不能为空");
        }
        RunningExecution? execution = _context.Runtime.Center.FindAny(runId.Trim());
        return execution is null
            ? McpToolResult.Failure("not_found", $"未找到运行任务：{runId}")
            : McpToolResult.Success(McpRunView.From(execution.Snapshot()));
    }

    [McpServerTool(Name = "list_history", Title = "查询运行历史", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("按最近天数和可选脚本/队列引用分页查询运行历史；天数受本地历史保留策略限制。")]
    public CallToolResult ListHistory(
        [Description("查询最近多少天，范围为 1 到本地历史保留上限。")]
        int days = 3,
        [Description("可选脚本稳定 ID 或唯一名称。")]
        string? scriptReference = null,
        [Description("可选队列稳定 ID 或唯一名称。")]
        string? queueReference = null,
        [Description("返回条数，范围为 1 到 200。")]
        int limit = 50,
        [Description("分页偏移，不能小于 0。")]
        int offset = 0)
    {
        if (days < 1 || days > AppFixedLimits.HistoryRetentionDaysMax)
        {
            return McpToolResult.Failure("validation_error", $"days 必须在 1 到 {AppFixedLimits.HistoryRetentionDaysMax} 之间");
        }
        if (limit < 1 || limit > 200 || offset < 0)
        {
            return McpToolResult.Failure("validation_error", "limit 必须在 1 到 200 之间，offset 不能小于 0");
        }
        string? scriptId = null;
        if (!string.IsNullOrWhiteSpace(scriptReference))
        {
            OperationResult<ScriptInstance> script = _context.ResolveScript(scriptReference);
            if (!script.Succeeded)
            {
                return McpToolResult.From(script);
            }
            scriptId = script.Value!.Id;
        }
        string? queueId = null;
        if (!string.IsNullOrWhiteSpace(queueReference))
        {
            OperationResult<DispatchQueue> queue = _context.ResolveQueue(queueReference);
            if (!queue.Succeeded)
            {
                return McpToolResult.From(queue);
            }
            queueId = queue.Value!.Id;
        }
        List<RunRecord> records = _context.Runtime.History.Query(
            DateTime.Today.AddDays(-(days - 1)),
            DateTime.Now.AddMinutes(5),
            scriptId,
            queueId);
        return McpToolResult.Success(new
        {
            total = records.Count,
            records = records.Skip(offset).Take(limit).ToList(),
        });
    }

    [McpServerTool(Name = "list_plugins", Title = "列出插件", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("列出插件元数据、能力、配置启用状态和运行状态。")]
    public CallToolResult ListPlugins()
    {
        return McpToolResult.Success(_context.Runtime.Plugins.PluginManagementViews);
    }

    [McpServerTool(Name = "get_settings", Title = "获取脱敏设置", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("读取应用设置；Webhook、SMTP 密码和访问令牌只返回已设置占位符，不返回明文。")]
    public CallToolResult GetSettings() => McpToolResult.Success(_context.GetSettings());

    [McpServerTool(Name = "get_update_status", Title = "获取更新状态", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("读取更新检查、下载和应用状态。")]
    public CallToolResult GetUpdateStatus() => McpToolResult.Success(_context.GetUpdateStatus());

    private async Task<CallToolResult> ReadPluginStoreAsync(bool forceRefresh)
    {
        try
        {
            PluginStoreSnapshot snapshot = await _context.Runtime.Resolve<PluginRepositoryService>()
                .GetStoreAsync(forceRefresh)
                .ConfigureAwait(false);
            return snapshot.Available
                ? McpToolResult.Success(McpViews.PluginStore(snapshot))
                : McpToolResult.Failure("repository_unavailable", snapshot.Error ?? "插件仓库暂不可用");
        }
        catch (Exception ex)
        {
            return McpToolResult.Exception(ex);
        }
    }
}
