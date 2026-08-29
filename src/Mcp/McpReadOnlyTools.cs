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

    [McpServerTool(Name = "get_script", Title = "获取脚本实例", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("按脚本稳定 ID 或唯一名称获取一个脚本实例。名称匹配多个对象时会返回 ambiguous_target。")]
    public CallToolResult GetScript([Description("脚本稳定 ID 或唯一名称。")]
        string reference)
    {
        OperationResult<ScriptInstance> result = _context.ResolveScript(reference);
        return McpToolResult.From(result, value => value is null ? null : McpViews.Script(value, _context.Users));
    }

    [McpServerTool(Name = "list_users", Title = "列出全局用户", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("列出全局用户、绑定数量、绑定设置和下一次队列计划。")]
    public CallToolResult ListUsers()
    {
        IReadOnlyList<DispatchQueue> queues = _context.Queues;
        return McpToolResult.Success(_context.Users.Select(user => McpViews.User(user, queues)).ToList());
    }

    [McpServerTool(Name = "get_user", Title = "获取全局用户", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("按全局用户稳定 ID 或唯一名称获取一个用户。")]
    public CallToolResult GetUser([Description("用户稳定 ID 或唯一名称。")]
        string reference)
    {
        OperationResult<NexusUser> result = _context.ResolveUser(reference);
        return McpToolResult.From(result, value => value is null ? null : McpViews.User(value, _context.Queues));
    }

    [McpServerTool(Name = "list_queues", Title = "列出调度队列", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("列出调度队列、任务顺序、时间表、通知开关和下一次触发时间。")]
    public CallToolResult ListQueues()
    {
        return McpToolResult.Success(_context.Queues.Select(McpViews.Queue).ToList());
    }

    [McpServerTool(Name = "get_queue", Title = "获取调度队列", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("按队列稳定 ID 或唯一名称获取一个调度队列。")]
    public CallToolResult GetQueue([Description("队列稳定 ID 或唯一名称。")]
        string reference)
    {
        OperationResult<DispatchQueue> result = _context.ResolveQueue(reference);
        return McpToolResult.From(result, value => value is null ? null : McpViews.Queue(value));
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
        if (days < 1 || days > Limits.Current.MaxHistoryRetentionDays)
        {
            return McpToolResult.Failure("validation_error", $"days 必须在 1 到 {Limits.Current.MaxHistoryRetentionDays} 之间");
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

    [McpServerTool(Name = "get_history_detail", Title = "获取历史详情", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("按历史记录 ID 获取运行详情与每次尝试的日志尾部。为控制响应大小，不返回完整日志文件。")]
    public CallToolResult GetHistoryDetail([Description("历史 RunRecord 的 ID。")]
        string recordId)
    {
        if (string.IsNullOrWhiteSpace(recordId))
        {
            return McpToolResult.Failure("validation_error", "recordId 不能为空");
        }
        RunRecord? record = _context.Runtime.History.FindById(recordId.Trim());
        return record is null
            ? McpToolResult.Failure("not_found", $"未找到历史记录：{recordId}")
            : McpToolResult.Success(_context.GetHistoryDetail(record));
    }

    [McpServerTool(Name = "list_plugins", Title = "列出插件", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("列出插件元数据、能力、配置启用状态和运行状态。")]
    public CallToolResult ListPlugins()
    {
        return McpToolResult.Success(_context.Runtime.Plugins.PluginManagementViews);
    }

    [McpServerTool(Name = "list_plugin_store", Title = "列出插件商店", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("读取官方插件 catalog、兼容性、安装状态、待重启事务和更新状态。")]
    public async Task<CallToolResult> ListPluginStore()
    {
        return await ReadPluginStoreAsync(forceRefresh: false).ConfigureAwait(false);
    }

    [McpServerTool(Name = "refresh_plugin_store", Title = "刷新插件商店", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("从官方插件仓库刷新 catalog 缓存并返回最新商店状态。")]
    public async Task<CallToolResult> RefreshPluginStore()
    {
        return await ReadPluginStoreAsync(forceRefresh: true).ConfigureAwait(false);
    }

    [McpServerTool(Name = "get_user_global_settings", Title = "获取用户全局设置", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("按全局用户稳定 ID 或唯一名称读取用户级绑定覆盖设置。")]
    public CallToolResult GetUserGlobalSettings(
        [Description("用户稳定 ID 或唯一名称。")]
        string userReference)
    {
        OperationResult<NexusUser> user = _context.ResolveUser(userReference);
        if (!user.Succeeded)
        {
            return McpToolResult.From(user);
        }
        return McpToolResult.Success((user.Value!.BindingOverrides ?? new UserBindingOverrides()).Clone());
    }

    [McpServerTool(Name = "list_plugin_user_settings", Title = "列出插件用户设置", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("读取指定用户的全部插件声明式全局设置；密钥字段只返回 configured 状态。")]
    public async Task<CallToolResult> ListPluginUserSettings(
        [Description("用户稳定 ID 或唯一名称。")]
        string userReference)
    {
        OperationResult<NexusUser> user = _context.ResolveUser(userReference);
        if (!user.Succeeded)
        {
            return McpToolResult.From(user);
        }
        OperationResult<IReadOnlyList<PluginUserGlobalSettingsView>> result = await _context.UserGlobalSettings
            .ReadAsync(user.Value!.Id)
            .ConfigureAwait(false);
        return McpToolResult.From(result);
    }

    [McpServerTool(Name = "get_plugin_user_settings", Title = "获取插件用户设置", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("读取指定用户的一个插件设置贡献；密钥字段只返回 configured 状态。")]
    public async Task<CallToolResult> GetPluginUserSettings(
        [Description("用户稳定 ID 或唯一名称。")]
        string userReference,
        [Description("插件稳定名称。")]
        string pluginName,
        [Description("插件设置贡献 ID。")]
        string contributionId)
    {
        OperationResult<NexusUser> user = _context.ResolveUser(userReference);
        if (!user.Succeeded)
        {
            return McpToolResult.From(user);
        }
        OperationResult<PluginUserGlobalSettingsView> result = await _context.UserGlobalSettings
            .ReadOneAsync(user.Value!.Id, pluginName?.Trim() ?? "", contributionId?.Trim() ?? "")
            .ConfigureAwait(false);
        return McpToolResult.From(result);
    }

    [McpServerTool(Name = "get_settings", Title = "获取脱敏设置", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("读取应用设置；Webhook、SMTP 密码和访问令牌只返回已设置占位符，不返回明文。")]
    public CallToolResult GetSettings() => McpToolResult.Success(_context.GetSettings());

    [McpServerTool(Name = "list_legacy_user_data", Title = "列出遗留用户数据", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("列出可确认清理的历史用户名目录候选；该工具不会删除任何数据。")]
    public CallToolResult ListLegacyUserData()
    {
        IReadOnlyList<LegacyDataCandidate> candidates = _context.Runtime.Resolve<UserDataPruner>().FindCandidates();
        return McpToolResult.Success(candidates.Select(item => new
        {
            item.ScriptId,
            item.UserKey,
            item.ItemCount,
        }).ToList());
    }

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
