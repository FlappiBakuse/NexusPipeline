using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.App;
using NexusPipeline.Localization;

namespace NexusPipeline.Cli;

internal static partial class CliCommandRouter
{
    private static int ExecuteSystemAction(CliArguments args)
    {
        if (!EnsurePositionals(args, 2, CliText.Get("error.requires_subcommand", "{command}需要一个子命令", ("command", "system-action")))
            || !EnsureOptions(args))
        {
            return CliExitCodes.For("invalid_arguments");
        }
        if (!string.Equals(Positional(args, 1), "cancel", StringComparison.OrdinalIgnoreCase))
        {
            return CliOutput.WriteFailure(
                "invalid_arguments",
                CliText.Get("error.action_not_supported", "当前操作不支持：{action}", ("action", Positional(args, 1) ?? "")));
        }
        return ReturnApi(
            new CliApiClient().Post("/api/system-action/cancel"),
            CliText.Get("success.system_action_cancelled", "已取消待执行系统操作"));
    }

}
