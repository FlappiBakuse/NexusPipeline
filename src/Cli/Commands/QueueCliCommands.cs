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
            return CliOutput.WriteFailure(
                "invalid_arguments",
                CliText.Get("error.missing_subcommand", "缺少 {command} 子命令（{commands}）",
                    ("command", "queue"),
                    ("commands", "list/get/create/update/delete/reorder")));
        }
        var client = new CliApiClient();
        switch (sub.ToLowerInvariant())
        {
            case "list":
                if (!EnsurePositionals(args, 2, CliText.Get("error.extra_arguments", "{usage} 不接受额外参数", ("usage", "queue list")))
                    || !EnsureOptions(args))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                return ReturnApi(client.Get("/api/queues"));
            case "get":
            {
                if (!EnsurePositionals(args, 3, CliText.Get("error.requires_target", "{usage}需要一个目标", ("usage", "queue get")))
                    || !EnsureOptions(args))
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
                if (!EnsurePositionals(args, 2, CliText.Get("error.extra_arguments", "{usage} 不接受额外参数", ("usage", "queue create"))))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                return SendFileMutation(client, args, "POST", "/api/queues", "调度队列");
            case "update":
            {
                if (!EnsurePositionals(args, 3, CliText.Get("error.requires_target", "{usage}需要一个目标", ("usage", "queue update"))))
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
                if (!EnsurePositionals(args, 3, CliText.Get("error.requires_target", "{usage}需要一个目标", ("usage", "queue delete")))
                    || !EnsureOptions(args))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                if (!TryRequirePositional(args, 2, "队列 ID 或名称", out string reference, out int error)
                    || !TryResolveTarget(client, "/api/queues", reference, "调度队列", out string id, out error))
                {
                    return error;
                }
                return ReturnApi(
                    client.Delete($"/api/queues/{Escape(id)}"),
                    CliText.Get("success.deleted", "{label}已删除", ("label", CliText.Resource("queue"))));
            }
            case "reorder":
                if (!EnsurePositionals(args, 2, CliText.Get("error.extra_arguments", "{usage} 不接受额外参数", ("usage", "queue reorder"))))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                return SendIds(client, args, "/api/queues/order", "调度队列");
            default:
                return CliOutput.WriteFailure(
                    "invalid_arguments",
                    CliText.Get("error.unknown_subcommand", "未知 {command} 子命令：{subcommand}",
                        ("command", "queue"),
                        ("subcommand", sub)));
        }
    }

}
