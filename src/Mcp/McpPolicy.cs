using System.Text.Json.Nodes;
using NexusPipeline.App.Contracts;
using NexusPipeline.Models;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Plugins;

namespace NexusPipeline.Mcp;

/// <summary>MCP 专用行为级安全策略。工具元数据之外，领域对象进入应用命令前再次检查。</summary>
internal static class McpPolicy
{
    /// <summary>
    /// 校验 MCP 即将执行的队列快照。队列可能由 Web/桌面路径创建，不能只依赖 MCP 写入时的校验；
    /// 执行入口也必须再次拦截完成后的休眠、重启、关机和退出操作。
    /// </summary>
    public static OperationResult<DispatchQueue> ValidateQueueExecution(DispatchQueue queue)
    {
        return ValidateQueueExecution(queue, "MCP 队列执行不允许触发完成后的系统操作；请在本地管理页面配置");
    }

    private static OperationResult<DispatchQueue> ValidateQueueExecution(DispatchQueue queue, string message)
    {
        if (!string.Equals(queue.CompletionAction, "none", StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<DispatchQueue>.Failure(
                "dangerous_completion_action",
                message,
                OperationErrorKind.Forbidden);
        }
        return OperationResult<DispatchQueue>.Ok(queue);
    }
}
