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
        if (!EnsurePositionals(args, rawSub is null ? 1 : 2, "update 只接受一个子命令"))
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
            "download" => ReturnApi(client.Post("/api/update/download"), "更新下载已启动"),
            "cancel" => ReturnApi(client.Post("/api/update/cancel"), "已发送取消下载请求"),
            "apply" => ReturnApi(client.Post("/api/update/apply", Object(("defer", args.Has("defer")))), "更新应用请求已提交"),
            _ => CliOutput.WriteFailure("invalid_arguments", $"未知 update 子命令：{sub}"),
        };
    }

}
