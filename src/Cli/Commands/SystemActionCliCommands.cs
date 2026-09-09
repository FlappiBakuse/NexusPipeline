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
        if (!EnsurePositionals(args, 2, "system-action 需要一个子命令") || !EnsureOptions(args))
        {
            return CliExitCodes.For("invalid_arguments");
        }
        if (!string.Equals(Positional(args, 1), "cancel", StringComparison.OrdinalIgnoreCase))
        {
            return CliOutput.WriteFailure("invalid_arguments", "system-action 当前只支持 cancel");
        }
        return ReturnApi(new CliApiClient().Post("/api/system-action/cancel"), "已取消待执行系统操作");
    }

}
