using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.App;
using NexusPipeline.Localization;

namespace NexusPipeline.Cli;

internal static partial class CliCommandRouter
{
    private static int ExecuteStatus(CliArguments args)
    {
        if (!EnsurePositionals(args, 1, "status 不接受位置参数") || !EnsureOptions(args))
        {
            return CliExitCodes.For("invalid_arguments");
        }
        return ReturnApi(new CliApiClient().Get("/api/status"));
    }

    private static int ExecuteDoctor(CliArguments args)
    {
        string? sub = Positional(args, 1)?.ToLowerInvariant();
        var client = new CliApiClient();
        if (sub is null)
        {
            return EnsurePositionals(args, 1, "doctor 不接受位置参数") && EnsureOptions(args)
                ? ReturnApi(client.Get("/api/diagnostics"))
                : CliExitCodes.For("invalid_arguments");
        }
        if (!sub.Equals("export", StringComparison.OrdinalIgnoreCase))
        {
            return CliOutput.WriteFailure("invalid_arguments", $"未知 doctor 子命令：{sub}");
        }
        if (!EnsurePositionals(args, 2, "doctor export 不接受额外参数")
            || !EnsureOptions(args, "output"))
        {
            return CliExitCodes.For("invalid_arguments");
        }
        JsonNode? body = args.Get("output") is string output && !string.IsNullOrWhiteSpace(output)
            ? Object(("outputPath", output))
            : null;
        return ReturnApi(client.Post("/api/diagnostics/export", body), "诊断包已导出");
    }

}
