using NexusPipeline.App.Contracts;
using NexusPipeline.Models;

namespace NexusPipeline.Mcp;

/// <summary>MCP 专用行为级安全策略。工具元数据之外，领域对象进入应用命令前再次检查。</summary>
internal static class McpPolicy
{
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "webhookUrl", "webhookSecret", "smtpPassword", "accessToken",
    };

    public static OperationResult<T> DestructiveDenied<T>()
    {
        return OperationResult<T>.Failure(
            "destructive_tools_disabled",
            "MCP 破坏性工具未启用，请在本地设置中显式打开 McpAllowDestructiveTools 后重启服务",
            OperationErrorKind.Forbidden);
    }

    public static OperationResult<DispatchQueue> ValidateQueue(DispatchQueue queue)
    {
        if (!string.Equals(queue.CompletionAction, "none", StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<DispatchQueue>.Failure(
                "dangerous_completion_action",
                "MCP 队列写入不允许设置完成后的系统操作；请在本地管理页面配置",
                OperationErrorKind.Forbidden);
        }
        return OperationResult<DispatchQueue>.Ok(queue);
    }

    public static bool IsSecretKey(string key) => SecretKeys.Contains(key.Trim());
}
