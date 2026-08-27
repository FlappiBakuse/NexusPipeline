using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NexusPipeline.App.Commands;
using NexusPipeline.App.Contracts;
using NexusPipeline.Models;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using NexusPipeline.Services.Update;

namespace NexusPipeline.Mcp;

/// <summary>默认可用的执行、配置和安全设置 MCP 工具。</summary>
[McpServerToolType]
internal sealed class McpMutationTools
{
    private readonly McpToolContext _context;

    public McpMutationTools(McpToolContext context)
    {
        _context = context;
    }

    [McpServerTool(Name = "run_script", Title = "运行脚本", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("提交一个脚本实例运行并立即返回 runId。脚本引用支持稳定 ID 或唯一名称；使用 get_run 轮询状态。")]
    public CallToolResult RunScript(
        [Description("脚本稳定 ID 或唯一名称。")]
        string scriptReference,
        [Description("可选全局用户稳定 ID 或唯一名称；留空表示按默认执行规则运行。")]
        string? userReference = null)
    {
        OperationResult<ScriptInstance> script = _context.ResolveScript(scriptReference);
        if (!script.Succeeded)
        {
            return McpToolResult.From(script);
        }
        string? userName = null;
        if (!string.IsNullOrWhiteSpace(userReference))
        {
            OperationResult<NexusUser> user = _context.ResolveUser(userReference);
            if (!user.Succeeded)
            {
                return McpToolResult.From(user);
            }
            userName = user.Value!.Name;
        }

        try
        {
            RunningExecution execution = _context.Runtime.Commands.StartScript(
                script.Value!.Id,
                "mcp",
                Audit.Mcp,
                userName);
            return McpToolResult.Success(new
            {
                runId = execution.Id,
                execution.Kind,
                execution.TargetId,
                execution.TargetName,
                execution.Mode,
                status = execution.Status,
            });
        }
        catch (ExecutionAdmissionException ex)
        {
            return McpToolResult.Failure(
                ex.Failure.StableCode,
                ex.Failure.Message,
                ex.Failure.ConflictingRunId is null ? null : new[] { ex.Failure.ConflictingRunId });
        }
        catch (InvalidOperationException ex)
        {
            return McpToolResult.Failure("execution_validation_failed", ex.Message);
        }
        catch (Exception ex)
        {
            return McpToolResult.Exception(ex);
        }
    }

    [McpServerTool(Name = "run_queue", Title = "运行调度队列", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("提交一个调度队列运行并立即返回 runId。队列引用支持稳定 ID 或唯一名称；使用 get_run 轮询状态。")]
    public CallToolResult RunQueue([Description("队列稳定 ID 或唯一名称。")]
        string queueReference)
    {
        OperationResult<DispatchQueue> queue = _context.ResolveQueue(queueReference);
        if (!queue.Succeeded)
        {
            return McpToolResult.From(queue);
        }
        OperationResult<DispatchQueue> executionPolicy = McpPolicy.ValidateQueueExecution(queue.Value!);
        if (!executionPolicy.Succeeded)
        {
            return McpToolResult.From(executionPolicy);
        }
        try
        {
            RunningExecution execution = _context.Runtime.Commands.StartQueue(
                queue.Value!.Id,
                "mcp",
                Audit.Mcp);
            return McpToolResult.Success(new
            {
                runId = execution.Id,
                execution.Kind,
                execution.TargetId,
                execution.TargetName,
                execution.Mode,
                status = execution.Status,
            });
        }
        catch (ExecutionAdmissionException ex)
        {
            return McpToolResult.Failure(
                ex.Failure.StableCode,
                ex.Failure.Message,
                ex.Failure.ConflictingRunId is null ? null : new[] { ex.Failure.ConflictingRunId });
        }
        catch (InvalidOperationException ex)
        {
            return McpToolResult.Failure("execution_validation_failed", ex.Message);
        }
        catch (Exception ex)
        {
            return McpToolResult.Exception(ex);
        }
    }

    [McpServerTool(Name = "cancel_run", Title = "取消运行", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("向活动运行发送取消信号；运行结果仍需通过 get_run 读取最终状态。")]
    public CallToolResult CancelRun([Description("活动运行的 runId。")]
        string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return McpToolResult.Failure("validation_error", "runId 不能为空");
        }
        if (_context.Runtime.Center.Find(runId.Trim()) is null)
        {
            return McpToolResult.Failure("not_found", $"未找到活动运行：{runId}");
        }
        try
        {
            _context.Runtime.Commands.Cancel(runId.Trim(), Audit.Mcp);
            return McpToolResult.Success(new { runId = runId.Trim(), canceled = true });
        }
        catch (Exception ex)
        {
            return McpToolResult.Exception(ex);
        }
    }

    [McpServerTool(Name = "cancel_system_action", Title = "取消待执行系统操作", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("取消队列完成后尚未执行的休眠、重启或关机倒计时；不存在待执行操作时返回 canceled=false。")]
    public CallToolResult CancelSystemAction()
    {
        bool canceled = _context.Runtime.Center.CancelSystemAction(Audit.Mcp);
        return McpToolResult.Success(new { canceled });
    }

    [McpServerTool(Name = "create_script", Title = "创建脚本实例", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("创建脚本实例并通过既有应用命令完成路径、插件、超时和判断脚本校验。")]
    public CallToolResult CreateScript([Description("脚本实例字段。")]
        McpScriptInput input)
    {
        if (input is null)
        {
            return McpToolResult.Failure("validation_error", "脚本输入不能为空");
        }
        return McpToolResult.From(
            ScriptCommands.Create(input.ToModel(), Audit.Mcp),
            value => value is null ? null : McpViews.Script(value, _context.Users));
    }

    [McpServerTool(Name = "update_script", Title = "更新脚本实例", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("按脚本稳定 ID 或唯一名称更新脚本实例；运行中或配置编辑中会返回 resource_busy。")]
    public CallToolResult UpdateScript(
        [Description("脚本稳定 ID 或唯一名称。")]
        string reference,
        [Description("脚本实例的新字段。")]
        McpScriptInput input)
    {
        if (input is null)
        {
            return McpToolResult.Failure("validation_error", "脚本输入不能为空");
        }
        OperationResult<ScriptInstance> target = _context.ResolveScript(reference);
        if (!target.Succeeded)
        {
            return McpToolResult.From(target);
        }
        return McpToolResult.From(
            ScriptCommands.Update(target.Value!.Id, input.ToModel(), Audit.Mcp),
            value => value is null ? null : McpViews.Script(value, _context.Users));
    }

    [McpServerTool(Name = "create_user", Title = "创建全局用户", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("创建全局用户；自动签到字段按现有应用规则校验。")]
    public CallToolResult CreateUser([Description("用户字段。")]
        McpUserInput input)
    {
        if (input is null)
        {
            return McpToolResult.Failure("validation_error", "用户输入不能为空");
        }
        OperationResult<NexusUser> result = UserCommands.Create(
            input.Name,
            input.Remark,
            input.AutoCheckInEnabled,
            Audit.Mcp);
        return McpToolResult.From(result, value => value is null ? null : McpViews.User(value, _context.Queues));
    }

    [McpServerTool(Name = "update_user", Title = "更新全局用户", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("按全局用户稳定 ID 或唯一名称更新用户基本信息。")]
    public CallToolResult UpdateUser(
        [Description("用户稳定 ID 或唯一名称。")]
        string reference,
        [Description("用户的新字段。")]
        McpUserInput input)
    {
        if (input is null)
        {
            return McpToolResult.Failure("validation_error", "用户输入不能为空");
        }
        OperationResult<NexusUser> target = _context.ResolveUser(reference);
        if (!target.Succeeded)
        {
            return McpToolResult.From(target);
        }
        OperationResult<NexusUser> result = UserCommands.Update(
            target.Value!.Id,
            input.Name,
            input.Remark,
            input.AutoCheckInEnabled,
            Audit.Mcp);
        return McpToolResult.From(result, value => value is null ? null : McpViews.User(value, _context.Queues));
    }

    [McpServerTool(Name = "add_binding", Title = "添加用户脚本绑定", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("为全局用户添加一个脚本绑定；用户和脚本引用都支持稳定 ID 或唯一名称。")]
    public CallToolResult AddBinding(
        [Description("用户稳定 ID 或唯一名称。")]
        string userReference,
        [Description("绑定字段，ScriptInstanceId 为脚本稳定 ID 或唯一名称。")]
        McpBindingInput input)
    {
        if (input is null)
        {
            return McpToolResult.Failure("validation_error", "绑定输入不能为空");
        }
        OperationResult<NexusUser> user = _context.ResolveUser(userReference);
        if (!user.Succeeded)
        {
            return McpToolResult.From(user);
        }
        OperationResult<ScriptInstance> script = _context.ResolveScript(input.ScriptInstanceId);
        if (!script.Succeeded)
        {
            return McpToolResult.From(script);
        }
        OperationResult<UserScriptBinding> result = UserCommands.AddBinding(
            user.Value!.Id,
            input.ToModel(script.Value!.Id),
            Audit.Mcp);
        return McpToolResult.From(result, value => value is null ? null : McpViews.Binding(value));
    }

    [McpServerTool(Name = "update_binding", Title = "更新用户脚本绑定", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("更新全局用户已有脚本绑定；运行中的绑定会返回 resource_busy。")]
    public CallToolResult UpdateBinding(
        [Description("用户稳定 ID 或唯一名称。")]
        string userReference,
        [Description("脚本稳定 ID 或唯一名称。")]
        string scriptReference,
        [Description("绑定的新字段。")]
        McpBindingInput input)
    {
        if (input is null)
        {
            return McpToolResult.Failure("validation_error", "绑定输入不能为空");
        }
        OperationResult<NexusUser> user = _context.ResolveUser(userReference);
        if (!user.Succeeded)
        {
            return McpToolResult.From(user);
        }
        OperationResult<ScriptInstance> script = _context.ResolveScript(scriptReference);
        if (!script.Succeeded)
        {
            return McpToolResult.From(script);
        }
        OperationResult<UserScriptBinding> result = UserCommands.UpdateBinding(
            user.Value!.Id,
            script.Value!.Id,
            input.ToModel(script.Value.Id),
            Audit.Mcp);
        return McpToolResult.From(result, value => value is null ? null : McpViews.Binding(value));
    }

    [McpServerTool(Name = "create_queue", Title = "创建调度队列", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("创建调度队列并通过既有应用命令校验任务、定时设置和脚本引用。MCP 不允许配置完成后的系统操作。")]
    public CallToolResult CreateQueue([Description("队列字段。")]
        McpQueueInput input)
    {
        if (input is null)
        {
            return McpToolResult.Failure("validation_error", "队列输入不能为空");
        }
        OperationResult<DispatchQueue> candidate = BuildQueue(input);
        if (!candidate.Succeeded)
        {
            return McpToolResult.From(candidate);
        }
        return McpToolResult.From(
            QueueCommands.Create(candidate.Value!, Audit.Mcp),
            value => value is null ? null : McpViews.Queue(value));
    }

    [McpServerTool(Name = "update_queue", Title = "更新调度队列", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("按队列稳定 ID 或唯一名称更新调度队列；MCP 不允许配置完成后的系统操作。")]
    public CallToolResult UpdateQueue(
        [Description("队列稳定 ID 或唯一名称。")]
        string reference,
        [Description("队列的新字段。")]
        McpQueueInput input)
    {
        if (input is null)
        {
            return McpToolResult.Failure("validation_error", "队列输入不能为空");
        }
        OperationResult<DispatchQueue> target = _context.ResolveQueue(reference);
        if (!target.Succeeded)
        {
            return McpToolResult.From(target);
        }
        OperationResult<DispatchQueue> candidate = BuildQueue(input);
        if (!candidate.Succeeded)
        {
            return McpToolResult.From(candidate);
        }
        return McpToolResult.From(
            QueueCommands.Update(target.Value!.Id, candidate.Value!, Audit.Mcp),
            value => value is null ? null : McpViews.Queue(value));
    }

    [McpServerTool(Name = "update_safe_settings", Title = "更新安全设置", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("更新设置白名单中的非密钥字段。AllowRemoteAccess、AccessToken、MCP 破坏性开关和 MCP 端口不接受来自 MCP 的修改。")]
    public CallToolResult UpdateSafeSettings([Description("安全设置字段；省略字段保持原值。")]
        McpSafeSettingsPatch patch)
    {
        if (patch is null)
        {
            return McpToolResult.Failure("validation_error", "设置输入不能为空");
        }
        System.Text.Json.Nodes.JsonObject json = patch.ToPatch();
        if (json.Count == 0)
        {
            return McpToolResult.Failure("validation_error", "至少提供一个安全设置字段");
        }
        OperationResult<AppSettings> result = SettingsCommands.Update(json, Audit.Mcp);
        return McpToolResult.From(result, value => value is null ? null : McpViews.Settings(value));
    }

    [McpServerTool(Name = "check_update", Title = "检查更新", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("按当前更新源检查可用版本；联网操作完成后返回更新状态。")]
    public async Task<CallToolResult> CheckUpdate()
    {
        try
        {
            UpdateStatusSnapshot status = await _context.Runtime.Resolve<UpdateService>()
                .CheckAsync(Audit.Mcp)
                .ConfigureAwait(false);
            return McpToolResult.Success(new
            {
                state = status.State.ToString().ToLowerInvariant(),
                status.Current,
                status.Latest,
                status.Available,
                status.Channel,
                status.Notes,
                error = status.Error,
            });
        }
        catch (Exception ex)
        {
            return McpToolResult.Exception(ex);
        }
    }

    [McpServerTool(Name = "download_update", Title = "下载更新", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("开始下载已发现的更新到应用暂存区；使用 get_update_status 轮询进度。")]
    public CallToolResult DownloadUpdate()
    {
        string? error = _context.Runtime.Resolve<UpdateService>().StartDownload(Audit.Mcp);
        return error is null
            ? McpToolResult.Success(new { accepted = true })
            : McpToolResult.Failure("update_busy", error);
    }

    [McpServerTool(Name = "cancel_update", Title = "取消更新下载", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("取消正在进行的更新检查或下载。")]
    public CallToolResult CancelUpdate() => McpToolResult.Success(new
    {
        canceled = _context.Runtime.Resolve<UpdateService>().CancelDownload(),
    });

    private OperationResult<DispatchQueue> BuildQueue(McpQueueInput input)
    {
        var scriptIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (McpQueueTaskInput task in input.Tasks ?? new List<McpQueueTaskInput>())
        {
            if (string.IsNullOrWhiteSpace(task.ScriptInstanceId))
            {
                continue;
            }
            OperationResult<ScriptInstance> script = _context.ResolveScript(task.ScriptInstanceId);
            if (!script.Succeeded)
            {
                OperationError error = script.Error!;
                return OperationResult<DispatchQueue>.Failure(error);
            }
            scriptIds[task.ScriptInstanceId.Trim()] = script.Value!.Id;
        }
        DispatchQueue queue = input.ToModel(scriptIds);
        return McpPolicy.ValidateQueue(queue);
    }
}
