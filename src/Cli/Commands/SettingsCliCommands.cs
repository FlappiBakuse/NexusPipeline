using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.App;
using NexusPipeline.Localization;

namespace NexusPipeline.Cli;

internal static partial class CliCommandRouter
{
    private static int ExecuteSettings(CliArguments args)
    {
        string? rawSub = Positional(args, 1);
        string sub = rawSub?.ToLowerInvariant() ?? "get";
        if (!EnsurePositionals(
                args,
                rawSub is null ? 1 : 2,
                CliText.Get("error.only_subcommand", "{command} 只接受一个子命令", ("command", "settings"))))
        {
            return CliExitCodes.For("invalid_arguments");
        }
        var client = new CliApiClient();
        return sub switch
        {
            "get" => ExecuteSettingsRead(args, client),
            "test" => ExecuteSettingsTest(args, client),
            "restart" => ExecuteSettingsRestart(args, client),
            "update" or "set" => ExecuteSettingsUpdate(args, client),
            _ => CliOutput.WriteFailure(
                "invalid_arguments",
                CliText.Get("error.unknown_subcommand", "未知 {command} 子命令：{subcommand}",
                    ("command", "settings"),
                    ("subcommand", sub))),
        };
    }

    private static int ExecuteSettingsRead(CliArguments args, CliApiClient client)
    {
        return EnsureOptions(args)
            ? ReturnApi(client.Get("/api/settings"))
            : CliExitCodes.For("invalid_arguments");
    }

    private static int ExecuteSettingsTest(CliArguments args, CliApiClient client)
    {
        return EnsureOptions(args)
            ? ReturnApi(client.Post("/api/settings/test"))
            : CliExitCodes.For("invalid_arguments");
    }

    private static int ExecuteSettingsRestart(CliArguments args, CliApiClient client)
    {
        return EnsureOptions(args)
            ? ReturnApi(client.Post("/api/settings/restart"), CliText.Get("success.settings_restart_requested", "服务重启请求已提交"))
            : CliExitCodes.For("invalid_arguments");
    }

    private static int ExecuteSettingsUpdate(CliArguments args, CliApiClient client)
    {
        if (!EnsureOptions(args, "file", "secret-key", "secret-value"))
        {
            return CliExitCodes.For("invalid_arguments");
        }
        JsonObject body;
        if (args.Has("file"))
        {
            if (!TryReadJsonObject(args, out JsonObject? fileBody, out int error))
            {
                return error;
            }
            body = fileBody!;
        }
        else
        {
            if (!TryRequireOption(args, "secret-key", "密钥字段名", out string key, out int error)
                || !TryRequireOption(args, "secret-value", "密钥值", out string value, out error))
            {
                return error;
            }
            body = Object(("secretKey", key), ("secretValue", value));
        }
        return ReturnApi(client.Put("/api/settings", body), CliText.Get("success.settings_updated", "设置已更新"));
    }

}
