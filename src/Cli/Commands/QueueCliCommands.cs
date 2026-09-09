using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.App;
using NexusPipeline.Localization;

namespace NexusPipeline.Cli;

internal static partial class CliCommandRouter
{
    private static int ExecuteQueue(CliArguments args)
    {
        string? sub = Positional(args, 1);
        if (sub is null)
        {
            return CliOutput.WriteFailure("invalid_arguments", "缺少 queue 子命令（list/get/create/update/delete/reorder）");
        }
        var client = new CliApiClient();
        switch (sub.ToLowerInvariant())
        {
            case "list":
                if (!EnsurePositionals(args, 2, "queue list 不接受额外参数") || !EnsureOptions(args))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                return ReturnApi(client.Get("/api/queues"));
            case "get":
            {
                if (!EnsurePositionals(args, 3, "queue get 需要一个目标") || !EnsureOptions(args))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                if (!TryRequirePositional(args, 2, "队列 ID 或名称", out string reference, out int error)
                    || !TryResolveTarget(client, "/api/queues", reference, "调度队列", out string id, out error))
                {
                    return error;
                }
                return ReturnApi(client.Get($"/api/queues/{Escape(id)}"));
            }
            case "create":
                if (!EnsurePositionals(args, 2, "queue create 不接受额外参数"))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                return SendFileMutation(client, args, "POST", "/api/queues", "调度队列");
            case "update":
            {
                if (!EnsurePositionals(args, 3, "queue update 需要一个目标"))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                if (!TryRequirePositional(args, 2, "队列 ID 或名称", out string reference, out int error)
                    || !TryResolveTarget(client, "/api/queues", reference, "调度队列", out string id, out error))
                {
                    return error;
                }
                return SendFileMutation(client, args, "PUT", $"/api/queues/{Escape(id)}", "调度队列");
            }
            case "delete":
            {
                if (!EnsurePositionals(args, 3, "queue delete 需要一个目标") || !EnsureOptions(args))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                if (!TryRequirePositional(args, 2, "队列 ID 或名称", out string reference, out int error)
                    || !TryResolveTarget(client, "/api/queues", reference, "调度队列", out string id, out error))
                {
                    return error;
                }
                return ReturnApi(client.Delete($"/api/queues/{Escape(id)}"), "调度队列已删除");
            }
            case "reorder":
                if (!EnsurePositionals(args, 2, "queue reorder 不接受额外参数"))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                return SendIds(client, args, "/api/queues/order", "调度队列");
            default:
                return CliOutput.WriteFailure("invalid_arguments", $"未知 queue 子命令：{sub}");
        }
    }

}
