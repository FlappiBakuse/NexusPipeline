using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NexusPipeline.App.Commands;
using NexusPipeline.App.Contracts;
using NexusPipeline.Models;
using NexusPipeline.Plugins;
using NexusPipeline.Services;
using NexusPipeline.Services.Update;

namespace NexusPipeline.Mcp;

/// <summary>只有 McpAllowDestructiveTools=true 且服务重启后才注册的高风险 MCP 工具。</summary>
[McpServerToolType]
internal sealed class McpDestructiveTools
{
    private readonly McpToolContext _context;

    public McpDestructiveTools(McpToolContext context)
    {
        _context = context;
    }

    [McpServerTool(Name = "delete_script", Title = "删除脚本实例", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("删除脚本实例及其用户配置数据；仅在破坏性工具显式启用时可用。")]
    public CallToolResult DeleteScript([Description("脚本稳定 ID 或唯一名称。")]
        string reference)
    {
        if (!Allowed())
        {
            return Denied();
        }
        OperationResult<ScriptInstance> target = _context.ResolveScript(reference);
        if (!target.Succeeded)
        {
            return McpToolResult.From(target);
        }
        OperationResult<ScriptInstance?> result = ScriptCommands.Delete(target.Value!.Id, Audit.Mcp);
        return McpToolResult.From(result, value => new
        {
            deleted = value is not null,
            scriptId = target.Value.Id,
        });
    }

    [McpServerTool(Name = "delete_user", Title = "删除全局用户", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("删除全局用户及其绑定数据；必须完整提供用户名作为二次确认。")]
    public CallToolResult DeleteUser(
        [Description("用户稳定 ID 或唯一名称。")]
        string reference,
        [Description("必须与当前用户名完全一致的确认文本。")]
        string confirmName)
    {
        if (!Allowed())
        {
            return Denied();
        }
        OperationResult<NexusUser> target = _context.ResolveUser(reference);
        if (!target.Succeeded)
        {
            return McpToolResult.From(target);
        }
        OperationResult<bool> result = UserCommands.Delete(target.Value!.Id, confirmName, Audit.Mcp);
        return McpToolResult.From(result, value => new
        {
            deleted = value,
            userId = target.Value.Id,
        });
    }

    [McpServerTool(Name = "delete_binding", Title = "删除用户脚本绑定", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("删除全局用户与脚本之间的绑定。")]
    public CallToolResult DeleteBinding(
        [Description("用户稳定 ID 或唯一名称。")]
        string userReference,
        [Description("脚本稳定 ID 或唯一名称。")]
        string scriptReference)
    {
        if (!Allowed())
        {
            return Denied();
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
        OperationResult<bool> result = UserCommands.DeleteBinding(
            user.Value!.Id,
            script.Value!.Id,
            Audit.Mcp);
        return McpToolResult.From(result, value => new
        {
            deleted = value,
            userId = user.Value.Id,
            scriptId = script.Value.Id,
        });
    }

    [McpServerTool(Name = "delete_queue", Title = "删除调度队列", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("删除调度队列及其定时计划。")]
    public CallToolResult DeleteQueue([Description("队列稳定 ID 或唯一名称。")]
        string reference)
    {
        if (!Allowed())
        {
            return Denied();
        }
        OperationResult<DispatchQueue> target = _context.ResolveQueue(reference);
        if (!target.Succeeded)
        {
            return McpToolResult.From(target);
        }
        OperationResult<DispatchQueue?> result = QueueCommands.Delete(target.Value!.Id, Audit.Mcp);
        return McpToolResult.From(result, value => new
        {
            deleted = value is not null,
            queueId = target.Value.Id,
        });
    }

    [McpServerTool(Name = "prune_legacy_user_data", Title = "清理遗留用户数据", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("删除指定脚本下的历史用户名目录；服务会再次校验路径、绑定、运行、编辑会话和配置交换锁。")]
    public CallToolResult PruneLegacyUserData(
        [Description("脚本稳定 ID。")]
        string scriptId,
        [Description("遗留目录的用户键，只能是候选清单中的目录名。")]
        string userKey)
    {
        if (!Allowed())
        {
            return Denied();
        }
        PruneResult result = _context.Runtime.Resolve<UserDataPruner>().Prune(
            scriptId.Trim(),
            userKey.Trim(),
            Audit.Mcp);
        return result.Succeeded
            ? McpToolResult.Success(new { pruned = true, scriptId, userKey })
            : McpToolResult.Failure(result.Code ?? "prune_failed", result.Error ?? "遗留数据清理失败");
    }

    [McpServerTool(Name = "set_secret", Title = "设置密钥", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("设置 Webhook、SMTP、代理密码或访问令牌；密钥值只进入加密存储，工具结果与审计日志不回显。")]
    public CallToolResult SetSecret(
        [Description("密钥字段：webhookUrl、webhookSecret、smtpPassword、proxyPassword 或 accessToken。")]
        string secretKey,
        [Description("密钥明文；不会出现在返回值或审计日志中。")]
        string secretValue)
    {
        if (!Allowed())
        {
            return Denied();
        }
        string key = secretKey?.Trim() ?? "";
        if (!McpPolicy.IsSecretKey(key))
        {
            return McpToolResult.Failure("validation_error", "不支持的密钥字段");
        }
        if (string.IsNullOrWhiteSpace(secretValue))
        {
            return McpToolResult.Failure("validation_error", "secretValue 不能为空；清除密钥请使用 clear_secret");
        }
        var patch = new JsonObject
        {
            ["secretKey"] = key,
            ["secretValue"] = secretValue,
        };
        OperationResult<AppSettings> result = SettingsCommands.Update(patch, Audit.Mcp);
        return McpToolResult.From(result, _ => new { key, configured = true });
    }

    [McpServerTool(Name = "clear_secret", Title = "清除密钥", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("清除 Webhook、SMTP、代理密码或访问令牌；返回值不包含密钥内容。")]
    public CallToolResult ClearSecret([Description("密钥字段：webhookUrl、webhookSecret、smtpPassword、proxyPassword 或 accessToken。")]
        string secretKey)
    {
        if (!Allowed())
        {
            return Denied();
        }
        string key = secretKey?.Trim() ?? "";
        if (!McpPolicy.IsSecretKey(key))
        {
            return McpToolResult.Failure("validation_error", "不支持的密钥字段");
        }
        var patch = new JsonObject
        {
            ["secretKey"] = key,
            ["secretValue"] = "",
        };
        OperationResult<AppSettings> result = SettingsCommands.Update(patch, Audit.Mcp);
        return McpToolResult.From(result, _ => new { key, configured = false });
    }

    [McpServerTool(Name = "enable_plugin", Title = "启用插件", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("修改插件配置为启用；插件在下次服务启动时应用。")]
    public CallToolResult EnablePlugin([Description("插件名称。")] string name) => SetPluginState(name, enabled: true);

    [McpServerTool(Name = "disable_plugin", Title = "禁用插件", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("修改插件配置为禁用；插件在下次服务启动时应用。")]
    public CallToolResult DisablePlugin([Description("插件名称。")] string name) => SetPluginState(name, enabled: false);

    private CallToolResult SetPluginState(string name, bool enabled)
    {
        if (!Allowed())
        {
            return Denied();
        }
        PluginManager plugins = _context.Runtime.Plugins;
        string pluginName = name?.Trim() ?? "";
        if (!plugins.IsKnownPlugin(pluginName))
        {
            return McpToolResult.Failure("not_found", $"插件不存在：{pluginName}");
        }
        if (!plugins.SetEnabled(pluginName, enabled, Audit.Mcp, out string? failureCode))
        {
            return McpToolResult.Failure(
                failureCode ?? "plugin_update_failed",
                failureCode == "host_maintenance"
                    ? "宿主正在进行维护操作，暂不能修改插件设置"
                    : $"插件状态保存失败：{pluginName}");
        }
        return McpToolResult.Success(new
        {
            name = pluginName,
            configuredEnabled = plugins.IsConfiguredEnabled(pluginName),
            runtimeEnabled = plugins.IsEnabled(pluginName),
            state = plugins.GetRuntimeState(pluginName),
            restartRequired = true,
        });
    }

    [McpServerTool(Name = "restart_service", Title = "重启服务", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("在安全退出门禁通过后拉起新进程并重启服务；仅支持托盘常驻服务模式。")]
    public CallToolResult RestartService()
    {
        if (!Allowed())
        {
            return Denied();
        }
        if (ApplicationHost.IsWebOnly)
        {
            return McpToolResult.Failure("operation_forbidden", "当前为仅网页模式，不支持自动重启，请手动重启");
        }
        if (_context.RequestRestart is null || !_context.RequestRestart())
        {
            return McpToolResult.Failure("service_busy", "服务当前不满足安全重启条件");
        }
        return McpToolResult.Success(new { accepted = true });
    }

    [McpServerTool(Name = "apply_update", Title = "应用更新", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("应用已经检查并下载完成的更新；默认登记为下次启动应用，以便本次 MCP 调用正常返回。")]
    public CallToolResult ApplyUpdate([Description("true 表示登记下次启动应用；false 表示立即申请切换并退出宿主。")]
        bool defer = true)
    {
        if (!Allowed())
        {
            return Denied();
        }
        UpdateApplyResult result = _context.Runtime.Resolve<UpdateService>().RequestApply(defer, Audit.Mcp);
        return result.Succeeded
            ? McpToolResult.Success(new { accepted = true, deferred = result.Deferred })
            : McpToolResult.Failure(result.Code ?? "update_failed", result.Error ?? "应用更新失败");
    }

    private bool Allowed() => _context.AllowDestructiveTools;

    private static CallToolResult Denied() =>
        McpToolResult.From(McpPolicy.DestructiveDenied<bool>());
}
