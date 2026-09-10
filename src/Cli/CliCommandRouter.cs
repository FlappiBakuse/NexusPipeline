using NexusPipeline.Localization;

namespace NexusPipeline.Cli;

internal static partial class CliCommandRouter
{
    private static readonly IReadOnlyDictionary<string, Func<CliArguments, int>> CommandHandlers =
        new Dictionary<string, Func<CliArguments, int>>(StringComparer.Ordinal)
        {
            ["status"] = ExecuteStatus,
            ["doctor"] = ExecuteDoctor,
            ["script"] = ExecuteScript,
            ["scripts"] = ExecuteScript,
            ["user"] = ExecuteUser,
            ["users"] = ExecuteUser,
            ["queue"] = ExecuteQueue,
            ["queues"] = ExecuteQueue,
            ["run"] = ExecuteRun,
            ["history"] = ExecuteHistory,
            ["settings"] = ExecuteSettings,
            ["setting"] = ExecuteSettings,
            ["plugin"] = ExecutePlugin,
            ["plugins"] = ExecutePlugin,
            ["update"] = ExecuteUpdate,
            ["system-action"] = ExecuteSystemAction,
            ["help"] = _ => WriteUsage(),
        };

    public static int Run(string[] rawArgs)
    {
        using IDisposable localeScope = LocaleContext.Push(LocaleCatalog.HostLocale);
        CliOutput.Configure(rawArgs);
        if (!CliArguments.TryParse(rawArgs, out CliArguments? parsed, out string? parseError))
        {
            return CliOutput.WriteFailure(
                "invalid_arguments",
                parseError ?? CliText.Get("error.invalid_arguments", "命令行参数无效"));
        }

        if (parsed!.HelpRequested || parsed.Positionals.Count == 0)
        {
            return WriteUsage();
        }

        try
        {
            string command = parsed.Positionals[0].ToLowerInvariant();
            if (CommandHandlers.TryGetValue(command, out Func<CliArguments, int>? handler))
            {
                return handler(parsed);
            }
            return CliOutput.WriteFailure(
                    "invalid_arguments",
                    CliText.Get(
                        "error.unknown_command",
                        "未知命令：{command}",
                        ("command", parsed.Positionals[0])))
                .AlsoWriteUsage();
        }
        catch (Exception ex)
        {
            return CliOutput.WriteFailure(
                "internal_error",
                CliText.Get("error.command_failed", "命令执行失败：{detail}", ("detail", ex.Message)));
        }
    }
}
