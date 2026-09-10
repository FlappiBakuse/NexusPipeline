using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.App;
using NexusPipeline.Localization;

namespace NexusPipeline.Cli;

internal static partial class CliCommandRouter
{
    private static int ExecuteScript(CliArguments args)
    {
        string? sub = Positional(args, 1);
        if (sub is null)
        {
            return CliOutput.WriteFailure(
                "invalid_arguments",
                CliText.Get("error.missing_subcommand", "缺少 {command} 子命令（{commands}）",
                    ("command", "script"),
                    ("commands", "list/get/create/update/delete/reorder/probe")));
        }
        var client = new CliApiClient();
        switch (sub.ToLowerInvariant())
        {
            case "list":
                if (!EnsurePositionals(args, 2, CliText.Get("error.extra_arguments", "{usage} 不接受额外参数", ("usage", "script list")))
                    || !EnsureOptions(args))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                return ReturnApi(client.Get("/api/scripts"));
            case "get":
            {
                if (!EnsurePositionals(args, 3, CliText.Get("error.requires_target", "{usage}需要一个目标", ("usage", "script get")))
                    || !EnsureOptions(args))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                if (!TryRequirePositional(args, 2, "脚本 ID 或名称", out string reference, out int error))
                {
                    return error;
                }
                if (!TryResolveTarget(client, "/api/scripts", reference, "脚本实例", out string id, out error))
                {
                    return error;
                }
                return ReturnApi(client.Get($"/api/scripts/{Escape(id)}"));
            }
            case "create":
                if (!EnsurePositionals(args, 2, CliText.Get("error.extra_arguments", "{usage} 不接受额外参数", ("usage", "script create"))))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                return SendFileMutation(client, args, "POST", "/api/scripts", "脚本实例");
            case "update":
            {
                if (!EnsurePositionals(args, 3, CliText.Get("error.requires_target", "{usage}需要一个目标", ("usage", "script update"))))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                if (!TryRequirePositional(args, 2, "脚本 ID 或名称", out string reference, out int error)
                    || !TryResolveTarget(client, "/api/scripts", reference, "脚本实例", out string id, out error))
                {
                    return error;
                }
                return SendFileMutation(client, args, "PUT", $"/api/scripts/{Escape(id)}", "脚本实例");
            }
            case "delete":
            {
                if (!EnsurePositionals(args, 3, CliText.Get("error.requires_target", "{usage}需要一个目标", ("usage", "script delete")))
                    || !EnsureOptions(args))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                if (!TryRequirePositional(args, 2, "脚本 ID 或名称", out string reference, out int error))
                {
                    return error;
                }
                if (!TryResolveTarget(client, "/api/scripts", reference, "脚本实例", out string id, out error))
                {
                    return error;
                }
                return ReturnApi(
                    client.Delete($"/api/scripts/{Escape(id)}"),
                    CliText.Get("success.deleted", "{label}已删除", ("label", CliText.Resource("script"))));
            }
            case "reorder":
                if (!EnsurePositionals(args, 2, CliText.Get("error.extra_arguments", "{usage} 不接受额外参数", ("usage", "script reorder"))))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                return SendIds(client, args, "/api/scripts/order", "脚本实例");
            case "probe":
            {
                if (!EnsurePositionals(args, 2, CliText.Get("error.extra_arguments", "{usage} 不接受额外参数", ("usage", "script probe")))
                    || !EnsureOptions(args, "plugin", "root"))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                if (!TryRequireOption(args, "plugin", "专用插件标识", out string plugin, out int error)
                    || !TryRequireOption(args, "root", "脚本根目录", out string root, out error))
                {
                    return error;
                }
                return ReturnApi(client.Post("/api/scripts/probe", Object(
                    ("pluginType", plugin),
                    ("rootPath", root))));
            }
            default:
                return CliOutput.WriteFailure(
                    "invalid_arguments",
                    CliText.Get("error.unknown_subcommand", "未知 {command} 子命令：{subcommand}",
                        ("command", "script"),
                        ("subcommand", sub)));
        }
    }

}
