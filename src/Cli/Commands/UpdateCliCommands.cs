using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.App;
using NexusPipeline.Localization;

namespace NexusPipeline.Cli;

internal static partial class CliCommandRouter
{
    private static int ExecuteUpdate(CliArguments args)
    {
        string? rawSub = Positional(args, 1);
        string sub = (rawSub ?? "status").ToLowerInvariant();
        if (!EnsurePositionals(
                args,
                rawSub is null ? 1 : 2,
                CliText.Get("error.only_subcommand", "{command} 只接受一个子命令", ("command", "update"))))
        {
            return CliExitCodes.For("invalid_arguments");
        }
        var client = new CliApiClient();
        if (!EnsureOptions(args, sub == "apply" ? new[] { "defer" } : Array.Empty<string>()))
        {
            return CliExitCodes.For("invalid_arguments");
        }
        return sub switch
        {
            "status" => ReturnApi(client.Get("/api/update/status")),
            "check" => ReturnApi(client.Post("/api/update/check")),
            "download" => ReturnApi(client.Post("/api/update/download"), CliText.Get("success.update_download_started", "更新下载已启动")),
            "cancel" => ReturnApi(client.Post("/api/update/cancel"), CliText.Get("success.update_cancel_requested", "已发送取消下载请求")),
            "apply" => ReturnApi(client.Post("/api/update/apply", Object(("defer", args.Has("defer")))), CliText.Get("success.update_apply_requested", "更新应用请求已提交")),
            _ => CliOutput.WriteFailure(
                "invalid_arguments",
                CliText.Get("error.unknown_subcommand", "未知 {command} 子命令：{subcommand}",
                    ("command", "update"),
                    ("subcommand", sub))),
        };
    }

}
