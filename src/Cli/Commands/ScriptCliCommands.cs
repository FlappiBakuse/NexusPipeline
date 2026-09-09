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
            return CliOutput.WriteFailure("invalid_arguments", "缺少 script 子命令（list/get/create/update/delete/reorder/probe）");
        }
        var client = new CliApiClient();
        switch (sub.ToLowerInvariant())
        {
            case "list":
                if (!EnsurePositionals(args, 2, "script list 不接受额外参数") || !EnsureOptions(args))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                return ReturnApi(client.Get("/api/scripts"));
            case "get":
            {
                if (!EnsurePositionals(args, 3, "script get 需要一个目标") || !EnsureOptions(args))
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
                if (!EnsurePositionals(args, 2, "script create 不接受额外参数"))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                return SendFileMutation(client, args, "POST", "/api/scripts", "脚本实例");
            case "update":
            {
                if (!EnsurePositionals(args, 3, "script update 需要一个目标"))
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
                if (!EnsurePositionals(args, 3, "script delete 需要一个目标") || !EnsureOptions(args))
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
                return ReturnApi(client.Delete($"/api/scripts/{Escape(id)}"), "脚本实例已删除");
            }
            case "reorder":
                if (!EnsurePositionals(args, 2, "script reorder 不接受额外参数"))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                return SendIds(client, args, "/api/scripts/order", "脚本实例");
            case "probe":
            {
                if (!EnsurePositionals(args, 2, "script probe 不接受额外参数")
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
                return CliOutput.WriteFailure("invalid_arguments", $"未知 script 子命令：{sub}");
        }
    }

}
