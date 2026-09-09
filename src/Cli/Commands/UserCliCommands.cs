using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.App;
using NexusPipeline.Localization;

namespace NexusPipeline.Cli;

internal static partial class CliCommandRouter
{
    private static int ExecuteUser(CliArguments args)
    {
        string? sub = Positional(args, 1);
        if (sub is null)
        {
            return CliOutput.WriteFailure("invalid_arguments", "缺少 user 子命令（list/get/create/update/delete/reorder/avatar/binding/global-settings）");
        }
        var client = new CliApiClient();
        switch (sub.ToLowerInvariant())
        {
            case "list":
                if (!EnsurePositionals(args, 2, "user list 不接受额外参数") || !EnsureOptions(args))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                return ReturnApi(client.Get("/api/users"));
            case "get":
            {
                if (!EnsurePositionals(args, 3, "user get 需要一个目标") || !EnsureOptions(args))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                if (!TryRequirePositional(args, 2, "用户 ID 或名称", out string reference, out int error)
                    || !TryResolveTarget(client, "/api/users", reference, "用户", out string id, out error))
                {
                    return error;
                }
                return ReturnApi(client.Get($"/api/users/{Escape(id)}"));
            }
            case "create":
            {
                if (!EnsurePositionals(args, 2, "user create 不接受额外参数")
                    || !EnsureOptions(args, "name", "remark"))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                if (!TryRequireOption(args, "name", "用户名", out string name, out int error))
                {
                    return error;
                }
                JsonObject body = Object(("name", name), ("remark", args.Get("remark") ?? ""));
                return ReturnApi(client.Post("/api/users", body), "用户已创建");
            }
            case "update":
            {
                if (!EnsurePositionals(args, 3, "user update 需要一个目标")
                    || !EnsureOptions(args, "name", "remark"))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                if (!TryRequirePositional(args, 2, "用户 ID 或名称", out string reference, out int error)
                    || !TryResolveTarget(client, "/api/users", reference, "用户", out string id, out error))
                {
                    return error;
                }
                CliApiResponse current = client.Get($"/api/users/{Escape(id)}");
                if (!current.Succeeded || current.Body is not JsonObject currentObject)
                {
                    return ReturnApi(current);
                }
                if (!args.Has("name") && !args.Has("remark"))
                {
                    return CliOutput.WriteFailure("invalid_arguments", "user update 至少需要 --name 或 --remark");
                }
                string nextName = args.Get("name") ?? currentObject["name"]?.ToString() ?? "";
                string nextRemark = args.Get("remark") ?? currentObject["remark"]?.ToString() ?? "";
                JsonObject body = Object(("name", nextName), ("remark", nextRemark));
                return ReturnApi(client.Put($"/api/users/{Escape(id)}", body), "用户已更新");
            }
            case "delete":
            {
                if (!EnsurePositionals(args, 3, "user delete 需要一个目标")
                    || !EnsureOptions(args, "confirm"))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                if (!TryRequirePositional(args, 2, "用户 ID 或名称", out string reference, out int error)
                    || !TryResolveTarget(client, "/api/users", reference, "用户", out string id, out error))
                {
                    return error;
                }
                if (!TryRequireOption(args, "confirm", "删除确认用户名", out string confirm, out error))
                {
                    return error;
                }
                return ReturnApi(client.Delete($"/api/users/{Escape(id)}", Object(("confirmName", confirm))), "用户已删除");
            }
            case "reorder":
                if (!EnsurePositionals(args, 2, "user reorder 不接受额外参数"))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                return SendIds(client, args, "/api/users/order", "用户");
            case "avatar":
                return ExecuteAvatar(args, client);
            case "binding":
                return ExecuteBinding(args, client);
            case "global-settings":
                return ExecuteUserGlobalSettings(args, client);
            default:
                return CliOutput.WriteFailure("invalid_arguments", $"未知 user 子命令：{sub}");
        }
    }

    private static int ExecuteUserGlobalSettings(CliArguments args, CliApiClient client)
    {
        string? action = Positional(args, 2)?.ToLowerInvariant();
        if (action is not ("get" or "update"))
        {
            return CliOutput.WriteFailure("invalid_arguments", "user global-settings 子命令必须为 get 或 update");
        }
        if (!EnsurePositionals(args, 4, $"user global-settings {action} 需要一个用户目标")
            || !EnsureOptions(args, action == "update" ? new[] { "file" } : Array.Empty<string>()))
        {
            return CliExitCodes.For("invalid_arguments");
        }
        if (!TryRequirePositional(args, 3, "用户 ID 或名称", out string reference, out int error)
            || !TryResolveTarget(client, "/api/users", reference, "用户", out string userId, out error))
        {
            return error;
        }
        string path = $"/api/users/{Escape(userId)}/global-settings";
        if (action == "get")
        {
            return ReturnApi(client.Get(path));
        }
        if (!TryReadJsonObject(args, out JsonObject? body, out error))
        {
            return error;
        }
        return ReturnApi(client.Put(path, body));
    }

    private static int ExecuteAvatar(CliArguments args, CliApiClient client)
    {
        string? first = Positional(args, 2);
        string? firstAction = first?.ToLowerInvariant();
        bool actionFirst = firstAction is "set" or "remove";
        string? action = actionFirst ? firstAction : Positional(args, 3)?.ToLowerInvariant();
        if (action is null)
        {
            return CliOutput.WriteFailure("invalid_arguments", "缺少 avatar 子命令（set/remove）");
        }
        if (!EnsurePositionals(args, 4, "user avatar 需要用户目标和操作"))
        {
            return CliExitCodes.For("invalid_arguments");
        }
        int referencePosition = actionFirst ? 3 : 2;
        if (!TryRequirePositional(args, referencePosition, "用户 ID 或名称", out string reference, out int error)
            || !TryResolveTarget(client, "/api/users", reference, "用户", out string userId, out error))
        {
            return error;
        }
        if (action.Equals("remove", StringComparison.OrdinalIgnoreCase))
        {
            if (!EnsureOptions(args))
            {
                return CliExitCodes.For("invalid_arguments");
            }
            return ReturnApi(client.Delete($"/api/users/{Escape(userId)}/avatar"), "用户头像已移除");
        }
        if (!action.Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            return CliOutput.WriteFailure("invalid_arguments", $"未知 avatar 子命令：{action}");
        }
        if (!EnsureOptions(args, "file"))
        {
            return CliExitCodes.For("invalid_arguments");
        }
        if (!TryReadFileBytes(args, out byte[]? bytes, out string? fileName, out error))
        {
            return error;
        }
        string mime = MimeFromExtension(Path.GetExtension(fileName));
        if (mime.Length == 0)
        {
            return CliOutput.WriteFailure("validation_error", "头像文件扩展名必须为 .png、.jpg/.jpeg 或 .webp");
        }
        JsonObject body = Object(("mimeType", mime), ("data", Convert.ToBase64String(bytes!)));
        return ReturnApi(client.Post($"/api/users/{Escape(userId)}/avatar", body), "用户头像已更新");
    }

    private static int ExecuteBinding(CliArguments args, CliApiClient client)
    {
        string? first = Positional(args, 2);
        string? firstAction = first?.ToLowerInvariant();
        bool actionFirst = firstAction is "list" or "add" or "update" or "delete" or "config";
        string? second = Positional(args, 3);
        bool configActionFirst = firstAction == "config"
            && second is "start" or "done" or "cancel";
        string? action = actionFirst ? firstAction : second?.ToLowerInvariant();
        if (action is null)
        {
            return CliOutput.WriteFailure("invalid_arguments", "缺少 binding 子命令（list/add/update/delete/config）");
        }

        if (action is not ("list" or "add" or "update" or "delete" or "config"))
        {
            return CliOutput.WriteFailure("invalid_arguments", $"未知 binding 子命令：{action}");
        }

        int userPosition = configActionFirst ? 4 : actionFirst ? 3 : 2;
        int scriptPosition = configActionFirst ? 5 : 4;
        int configActionPosition = configActionFirst ? 3 : 5;
        if (!TryRequirePositional(args, userPosition, "用户 ID 或名称", out string userReference, out int error)
            || !TryResolveTarget(client, "/api/users", userReference, "用户", out string userId, out error))
        {
            return error;
        }
        if (action.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            if (!EnsurePositionals(args, 4, "user binding list 不接受脚本参数") || !EnsureOptions(args))
            {
                return CliExitCodes.For("invalid_arguments");
            }
            return ReturnApi(client.Get($"/api/users/{Escape(userId)}/bindings"));
        }

        if (action.Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            if (!EnsurePositionals(args, 6, "user binding config 需要配置操作") || !EnsureOptions(args, "mode"))
            {
                return CliExitCodes.For("invalid_arguments");
            }
            if (!TryRequirePositional(args, scriptPosition, "脚本 ID 或名称", out string configScriptReference, out error)
                || !TryResolveTarget(client, "/api/scripts", configScriptReference, "脚本实例", out string configScriptId, out error))
            {
                return error;
            }
            string? configAction = Positional(args, configActionPosition)?.ToLowerInvariant();
            if (configAction is not ("start" or "done" or "cancel"))
            {
                return CliOutput.WriteFailure("invalid_arguments", "config 子命令必须为 start、done 或 cancel");
            }
            // v0.12.8：首次编辑（无配置快照）必须显式选择配置方式；--mode 缺省按 normal（有快照时行为不变）。
            string configMode = args.Get("mode")?.ToLowerInvariant() ?? "";
            if (configAction == "start" && configMode is not ("" or "normal" or "fresh" or "reuse"))
            {
                return CliOutput.WriteFailure("invalid_arguments", "--mode 仅支持 normal、fresh 或 reuse");
            }
            if (configAction != "start" && !string.IsNullOrEmpty(configMode))
            {
                return CliOutput.WriteFailure("invalid_arguments", "--mode 仅在 config start 时可用");
            }
            string configPath = $"/api/users/{Escape(userId)}/bindings/{Escape(configScriptId)}/edit-config";
            return ReturnApi(client.Post(configPath, Object(("action", configAction), ("mode", configMode))));
        }

        string scriptReference;
        if (action.Equals("add", StringComparison.OrdinalIgnoreCase) && args.Has("script"))
        {
            if (!EnsurePositionals(args, 4, "user binding add 需要一个用户目标")
                || !EnsureOptions(args, "file", "script"))
            {
                return CliExitCodes.For("invalid_arguments");
            }
            if (!TryRequireOption(args, "script", "脚本 ID 或名称", out scriptReference, out error))
            {
                return error;
            }
        }
        else
        {
            string[] allowed = action.Equals("update", StringComparison.OrdinalIgnoreCase)
                ? new[] { "file" }
                : Array.Empty<string>();
            if (!EnsurePositionals(args, 5, "user binding 操作需要完整目标参数")
                || !EnsureOptions(args, allowed))
            {
                return CliExitCodes.For("invalid_arguments");
            }
            if (!TryRequirePositional(args, scriptPosition, "脚本 ID 或名称", out scriptReference, out error))
            {
                return error;
            }
        }
        if (!TryResolveTarget(client, "/api/scripts", scriptReference, "脚本实例", out string scriptId, out error))
        {
            return error;
        }
        string bindingPath = $"/api/users/{Escape(userId)}/bindings/{Escape(scriptId)}";
        if (action.Equals("add", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryReadJsonObject(args, out JsonObject? body, out error))
            {
                return error;
            }
            body!["scriptInstanceId"] = scriptId;
            return ReturnApi(client.Post($"/api/users/{Escape(userId)}/bindings", body), "绑定已添加");
        }
        if (action.Equals("update", StringComparison.OrdinalIgnoreCase))
        {
            if (!EnsureOptions(args, "file"))
            {
                return CliExitCodes.For("invalid_arguments");
            }
            return SendFileMutation(client, args, "PUT", bindingPath, "用户绑定");
        }
        if (action.Equals("delete", StringComparison.OrdinalIgnoreCase))
        {
            return ReturnApi(client.Delete(bindingPath), "绑定已删除");
        }
        return CliOutput.WriteFailure("invalid_arguments", $"未知 binding 子命令：{action}");
    }

}
