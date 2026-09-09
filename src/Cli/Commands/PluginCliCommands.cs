using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.App;
using NexusPipeline.Localization;

namespace NexusPipeline.Cli;

internal static partial class CliCommandRouter
{
    private static int ExecutePlugin(CliArguments args)
    {
        var client = new CliApiClient();
        string? rawSub = Positional(args, 1);
        if (rawSub is null)
        {
            if (!EnsurePositionals(args, 1, "plugin list 不接受额外参数") || !EnsureOptions(args))
            {
                return CliExitCodes.For("invalid_arguments");
            }
            return ReturnApi(client.Get("/api/plugins"));
        }

        string first = rawSub.ToLowerInvariant();
        if (first == "store")
        {
            return ExecutePluginStore(args, client);
        }
        if (first == "user-settings")
        {
            return ExecutePluginUserSettings(args, client);
        }

        string? rawSecond = Positional(args, 2);
        string? second = rawSecond?.ToLowerInvariant();
        bool actionFirst = first is "get" or "enable" or "disable" or "install" or "update" or "uninstall";
        string sub = actionFirst ? first : second ?? first;
        if (sub == "list")
        {
            if (!EnsurePositionals(args, 2, "plugin list 不接受额外参数")
                || !EnsureOptions(args))
            {
                return CliExitCodes.For("invalid_arguments");
            }
            return ReturnApi(client.Get("/api/plugins"));
        }
        if (sub is not ("get" or "enable" or "disable" or "install" or "update" or "uninstall"))
        {
            return CliOutput.WriteFailure("invalid_arguments", $"未知 plugin 子命令：{sub}");
        }
        if (!EnsurePositionals(args, 3, "plugin 操作需要插件名称"))
        {
            return CliExitCodes.For("invalid_arguments");
        }
        int referencePosition = actionFirst ? 2 : 1;
        if (!TryRequirePositional(args, referencePosition, "插件名称", out string reference, out int error))
        {
            return error;
        }
        if (sub is "install" or "update" or "uninstall")
        {
            if (!EnsureOptions(args))
            {
                return CliExitCodes.For("invalid_arguments");
            }
            return ReturnApi(
                client.Post($"/api/plugins/store/{Escape(reference)}/{sub}"),
                $"插件商店操作已登记：{sub}");
        }
        CliApiResponse list = client.Get("/api/plugins");
        if (!TryResolvePlugin(list, reference, out JsonObject? match, out error))
        {
            return error;
        }
        if (sub == "get")
        {
            if (!EnsureOptions(args))
            {
                return CliExitCodes.For("invalid_arguments");
            }
            CliOutput.WriteSuccess(match);
            return 0;
        }
        string name = match!["name"]?.ToString() ?? reference;
        if (!EnsureOptions(args))
        {
            return CliExitCodes.For("invalid_arguments");
        }
        return ReturnApi(client.Post($"/api/plugins/{Escape(name)}/{sub}"), "插件设置已更新");
    }

    private static int ExecutePluginStore(CliArguments args, CliApiClient client)
    {
        string? action = Positional(args, 2)?.ToLowerInvariant();
        if (action is "list" or "refresh")
        {
            if (!EnsurePositionals(args, 3, $"plugin store {action} 不接受额外参数")
                || !EnsureOptions(args))
            {
                return CliExitCodes.For("invalid_arguments");
            }
            return action == "list"
                ? ReturnApi(client.Get("/api/plugins/store"))
                : ReturnApi(client.Post("/api/plugins/store/refresh"), "插件商店已刷新");
        }
        if (action is not ("install" or "update" or "uninstall"))
        {
            return CliOutput.WriteFailure("invalid_arguments", "plugin store 子命令必须为 list、refresh、install、update 或 uninstall");
        }
        if (!EnsurePositionals(args, 4, $"plugin store {action} 需要插件名称") || !EnsureOptions(args))
        {
            return CliExitCodes.For("invalid_arguments");
        }
        if (!TryRequirePositional(args, 3, "插件名称", out string name, out int error))
        {
            return error;
        }
        return ReturnApi(
            client.Post($"/api/plugins/store/{Escape(name)}/{action}"),
            $"插件商店操作已登记：{action}");
    }

    private static int ExecutePluginUserSettings(CliArguments args, CliApiClient client)
    {
        string? action = Positional(args, 2)?.ToLowerInvariant();
        if (action is not ("list" or "get" or "update"))
        {
            return CliOutput.WriteFailure("invalid_arguments", "plugin user-settings 子命令必须为 list、get 或 update");
        }
        int expected = action == "list" ? 4 : 6;
        if (!EnsurePositionals(args, expected, $"plugin user-settings {action} 参数数量不正确")
            || !EnsureOptions(args, action == "update" ? new[] { "file" } : Array.Empty<string>()))
        {
            return CliExitCodes.For("invalid_arguments");
        }
        if (!TryRequirePositional(args, 3, "用户 ID 或名称", out string userReference, out int error)
            || !TryResolveTarget(client, "/api/users", userReference, "用户", out string userId, out error))
        {
            return error;
        }
        string path = $"/api/plugin-contributions/user-global/{Escape(userId)}";
        if (action == "list")
        {
            return ReturnApi(client.Get(path));
        }

        string pluginName = Positional(args, 4) ?? "";
        string contributionId = Positional(args, 5) ?? "";
        CliApiResponse response = client.Get(path);
        if (!response.Succeeded || response.Body is not JsonArray settings)
        {
            return ReturnApi(response);
        }
        JsonObject? match = settings.OfType<JsonObject>().FirstOrDefault(item =>
            string.Equals(item["pluginName"]?.ToString(), pluginName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item["id"]?.ToString(), contributionId, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return CliOutput.WriteFailure("not_found", $"未找到插件设置贡献：{pluginName}/{contributionId}");
        }
        if (action == "get")
        {
            CliOutput.WriteSuccess(match);
            return 0;
        }
        if (!TryReadJsonObject(args, out JsonObject? values, out error))
        {
            return error;
        }
        return ReturnApi(
            client.Put($"{path}/{Escape(pluginName)}/{Escape(contributionId)}", Object(("values", values))),
            "插件用户设置已更新");
    }

    private static bool TryResolvePlugin(
        CliApiResponse response,
        string reference,
        out JsonObject? match,
        out int error)
    {
        match = null;
        if (!response.Succeeded)
        {
            error = ReturnApi(response);
            return false;
        }
        if (response.Body is not JsonArray plugins)
        {
            error = CliOutput.WriteFailure("internal_error", "服务返回的插件列表格式无效");
            return false;
        }
        JsonObject[] entries = plugins.OfType<JsonObject>().ToArray();
        match = entries.FirstOrDefault(plugin =>
            string.Equals(plugin["name"]?.ToString(), reference, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            error = 0;
            return true;
        }
        JsonObject[] aliases = entries.Where(plugin =>
            string.Equals(plugin["displayName"]?.ToString(), reference, StringComparison.OrdinalIgnoreCase)
            || string.Equals(plugin["artifactName"]?.ToString(), reference, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (aliases.Length == 1)
        {
            match = aliases[0];
            error = 0;
            return true;
        }
        if (aliases.Length > 1)
        {
            var candidates = new JsonArray(aliases.Select(plugin => (JsonNode?)new JsonObject
            {
                ["name"] = plugin["name"]?.ToString() ?? "",
                ["displayName"] = plugin["displayName"]?.ToString() ?? "",
            }).ToArray());
            error = CliOutput.WriteFailure("ambiguous_target", $"插件名称匹配到多个对象：{reference}", Object(("candidates", candidates)));
            return false;
        }
        error = CliOutput.WriteFailure("not_found", $"未找到插件：{reference}");
        return false;
    }

}
