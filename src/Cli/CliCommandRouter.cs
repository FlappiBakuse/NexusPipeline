using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.App;

namespace NexusPipeline.Cli;

/// <summary>正式 noun/subcommand CLI 路由。</summary>
internal static class CliCommandRouter
{
    public static int Run(string[] rawArgs)
    {
        CliOutput.Configure(rawArgs);
        if (!CliArguments.TryParse(rawArgs, out CliArguments? parsed, out string? parseError))
        {
            return CliOutput.WriteFailure("invalid_arguments", parseError ?? "命令行参数无效");
        }

        if (parsed!.HelpRequested || parsed.Positionals.Count == 0)
        {
            return WriteUsage();
        }

        try
        {
            string command = parsed.Positionals[0].ToLowerInvariant();
            return command switch
            {
                "status" => ExecuteStatus(parsed),
                "script" or "scripts" => ExecuteScript(parsed),
                "user" or "users" => ExecuteUser(parsed),
                "queue" or "queues" => ExecuteQueue(parsed),
                "run" => ExecuteRun(parsed),
                "history" => ExecuteHistory(parsed),
                "settings" or "setting" => ExecuteSettings(parsed),
                "plugin" or "plugins" => ExecutePlugin(parsed),
                "update" => ExecuteUpdate(parsed),
                "maintenance" => ExecuteMaintenance(parsed),
                "system-action" => ExecuteSystemAction(parsed),
                "help" => WriteUsage(),
                _ => CliOutput.WriteFailure("invalid_arguments", $"未知命令：{parsed.Positionals[0]}")
                    .AlsoWriteUsage(),
            };
        }
        catch (Exception ex)
        {
            return CliOutput.WriteFailure("internal_error", $"命令执行失败：{ex.Message}");
        }
    }

    private static int ExecuteStatus(CliArguments args)
    {
        if (!EnsurePositionals(args, 1, "status 不接受位置参数") || !EnsureOptions(args))
        {
            return CliExitCodes.For("invalid_arguments");
        }
        return ReturnApi(new CliApiClient().Get("/api/status"));
    }

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
            if (!EnsurePositionals(args, 6, "user binding config 需要配置操作") || !EnsureOptions(args))
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
            string configPath = $"/api/users/{Escape(userId)}/bindings/{Escape(configScriptId)}/edit-config";
            return ReturnApi(client.Post(configPath, Object(("action", configAction))));
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

    private static int ExecuteQueue(CliArguments args)
    {
        string? sub = Positional(args, 1);
        if (sub is null)
        {
            return CliOutput.WriteFailure("invalid_arguments", "缺少 queue 子命令（list/get/create/update/delete/reorder）");
        }
        var client = new CliApiClient();
        switch (sub.ToLowerInvariant())
        {
            case "list":
                if (!EnsurePositionals(args, 2, "queue list 不接受额外参数") || !EnsureOptions(args))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                return ReturnApi(client.Get("/api/queues"));
            case "get":
            {
                if (!EnsurePositionals(args, 3, "queue get 需要一个目标") || !EnsureOptions(args))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                if (!TryRequirePositional(args, 2, "队列 ID 或名称", out string reference, out int error)
                    || !TryResolveTarget(client, "/api/queues", reference, "调度队列", out string id, out error))
                {
                    return error;
                }
                return ReturnApi(client.Get($"/api/queues/{Escape(id)}"));
            }
            case "create":
                if (!EnsurePositionals(args, 2, "queue create 不接受额外参数"))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                return SendFileMutation(client, args, "POST", "/api/queues", "调度队列");
            case "update":
            {
                if (!EnsurePositionals(args, 3, "queue update 需要一个目标"))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                if (!TryRequirePositional(args, 2, "队列 ID 或名称", out string reference, out int error)
                    || !TryResolveTarget(client, "/api/queues", reference, "调度队列", out string id, out error))
                {
                    return error;
                }
                return SendFileMutation(client, args, "PUT", $"/api/queues/{Escape(id)}", "调度队列");
            }
            case "delete":
            {
                if (!EnsurePositionals(args, 3, "queue delete 需要一个目标") || !EnsureOptions(args))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                if (!TryRequirePositional(args, 2, "队列 ID 或名称", out string reference, out int error)
                    || !TryResolveTarget(client, "/api/queues", reference, "调度队列", out string id, out error))
                {
                    return error;
                }
                return ReturnApi(client.Delete($"/api/queues/{Escape(id)}"), "调度队列已删除");
            }
            case "reorder":
                if (!EnsurePositionals(args, 2, "queue reorder 不接受额外参数"))
                {
                    return CliExitCodes.For("invalid_arguments");
                }
                return SendIds(client, args, "/api/queues/order", "调度队列");
            default:
                return CliOutput.WriteFailure("invalid_arguments", $"未知 queue 子命令：{sub}");
        }
    }

    private static int ExecuteRun(CliArguments args)
    {
        string? rawSub = Positional(args, 1);
        string? sub = rawSub?.ToLowerInvariant();
        int error;
        if (sub is null)
        {
            return CliOutput.WriteFailure("invalid_arguments", "缺少 run 子命令（script/queue/get/list/cancel）");
        }
        if (sub == "get")
        {
            if (!EnsurePositionals(args, 3, "run get 需要运行 ID") || !EnsureOptions(args))
            {
                return CliExitCodes.For("invalid_arguments");
            }
            if (!TryRequirePositional(args, 2, "运行 ID", out string runId, out error))
            {
                return error;
            }
            return ReturnApi(new CliApiClient().Get($"/api/dispatch/{Escape(runId)}"));
        }
        if (sub == "list")
        {
            if (!EnsurePositionals(args, 2, "run list 不接受额外参数") || !EnsureOptions(args))
            {
                return CliExitCodes.For("invalid_arguments");
            }
            return ReturnApi(new CliApiClient().Get("/api/runs"));
        }
        if (sub == "cancel")
        {
            if (!EnsurePositionals(args, 3, "run cancel 需要运行 ID") || !EnsureOptions(args))
            {
                return CliExitCodes.For("invalid_arguments");
            }
            return ExecuteCancel(args);
        }
        if (sub is not ("script" or "queue"))
        {
            return CliOutput.WriteFailure("invalid_arguments", $"未知 run 子命令：{sub}");
        }

        int targetPosition = 2;
        if (!EnsurePositionals(args, targetPosition + 1, "run 操作需要一个目标")
            || !EnsureOptions(args, "mode", "auto", "manual", "user", "detach"))
        {
            return CliExitCodes.For("invalid_arguments");
        }
        if (!TryRequirePositional(args, targetPosition, sub == "script" ? "脚本 ID 或名称" : "队列 ID 或名称", out string reference, out error))
        {
            return error;
        }
        var client = new CliApiClient();
        string resource = sub == "script" ? "/api/scripts" : "/api/queues";
        string display = sub == "script" ? "脚本实例" : "调度队列";
        if (!TryResolveTarget(client, resource, reference, display, out string id, out error))
        {
            return error;
        }
        string mode = args.Get("mode")?.Equals("auto", StringComparison.OrdinalIgnoreCase) == true || args.Has("auto")
            ? "auto"
            : "manual";
        JsonObject body = sub == "script"
            ? Object(("scriptId", id), ("mode", mode), ("userName", args.Get("user") ?? ""))
            : Object(("queueId", id), ("mode", mode));
        CliApiResponse response = client.Post($"/api/dispatch/{sub}", body);
        if (!response.Succeeded)
        {
            return ReturnApi(response);
        }
        if (response.Body is not JsonObject dispatch || string.IsNullOrWhiteSpace(dispatch["runId"]?.ToString()))
        {
            return CliOutput.WriteFailure("internal_error", "服务已接受任务，但响应中没有有效 runId");
        }
        if (args.Has("detach"))
        {
            return ReturnApi(response, "任务已提交（detach）");
        }
        return PollRun(client, dispatch["runId"]!.ToString());
    }

    private static int ExecuteCancel(CliArguments args)
    {
        if (!EnsurePositionals(args, 3, "run cancel 需要运行 ID") || !EnsureOptions(args))
        {
            return CliExitCodes.For("invalid_arguments");
        }
        if (!TryRequirePositional(args, 2, "运行 ID", out string runId, out int error))
        {
            return error;
        }
        return ReturnApi(new CliApiClient().Post("/api/cancel", Object(("runId", runId))), "已发送取消请求");
    }

    private static int PollRun(CliApiClient client, string runId)
    {
        int timeoutSeconds = 6 * 60 * 60;
        if (client is null)
        {
            return CliOutput.WriteFailure("service_unavailable", "无法连接到常驻服务");
        }
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        string lastStatus = "";
        while (DateTime.UtcNow < deadline)
        {
            CliApiResponse response = client.Get($"/api/dispatch/{Escape(runId)}");
            if (!response.Succeeded)
            {
                return ReturnApi(response);
            }
            JsonNode? body = response.Body;
            string status = body?["status"]?.ToString() ?? "";
            string currentStatus = body?["currentStatus"]?.ToString() ?? "";
            if (currentStatus.Length > 0 && currentStatus != lastStatus)
            {
                lastStatus = currentStatus;
                CliOutput.WriteProgress($"运行 {runId}：{currentStatus}");
            }
            if (!status.Equals("running", StringComparison.OrdinalIgnoreCase))
            {
                bool cancelled = body?["records"] is JsonArray records
                    && records.Any(record => record?["status"]?.ToString() == "cancelled");
                bool failed = body?["records"] is JsonArray failedRecords
                    && failedRecords.Any(record => record?["status"]?.ToString() != "success");
                if (cancelled)
                {
                    return CliOutput.WriteFailure("cancelled", "运行已取消", body);
                }
                if (failed)
                {
                    return CliOutput.WriteFailure("execution_failed", "运行完成，但存在失败记录", body);
                }
                CliOutput.WriteSuccess(body, "运行已完成");
                return 0;
            }
            Thread.Sleep(1000);
        }
        return CliOutput.WriteFailure("timeout", "轮询运行结果超过 6 小时上限", Object(("runId", runId)));
    }

    private static int ExecuteHistory(CliArguments args)
    {
        string? rawSub = Positional(args, 1);
        string? sub = rawSub?.ToLowerInvariant();
        var client = new CliApiClient();
        if (sub is "dates")
        {
            if (!EnsurePositionals(args, 2, "history dates 不接受额外参数") || !EnsureOptions(args, "days"))
            {
                return CliExitCodes.For("invalid_arguments");
            }
            string query = Query(("days", args.Get("days") ?? "3"));
            return ReturnApi(client.Get("/api/history/dates" + query));
        }
        if (sub is "get" or "detail")
        {
            if (!EnsurePositionals(args, 3, "history get 需要历史记录 ID")
                || !EnsureOptions(args, "full", "attempt"))
            {
                return CliExitCodes.For("invalid_arguments");
            }
            if (!TryRequirePositional(args, 2, "历史记录 ID", out string id, out int error))
            {
                return error;
            }
            string query = Query(("id", id), ("full", args.Has("full") ? "true" : "false"), ("attempt", args.Get("attempt") ?? ""));
            return ReturnApi(client.Get("/api/history/detail" + query));
        }
        if (sub is not null && !sub.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            return CliOutput.WriteFailure("invalid_arguments", $"未知 history 子命令：{sub}");
        }
        if (!EnsurePositionals(args, rawSub is null ? 1 : 2, "history list 不接受额外参数")
            || !EnsureOptions(args, "date", "days", "script", "queue", "offset", "limit"))
        {
            return CliExitCodes.For("invalid_arguments");
        }
        string path = "/api/history" + Query(
            ("date", args.Get("date") ?? ""),
            ("days", args.Get("days") ?? "3"),
            ("scriptId", args.Get("script") ?? ""),
            ("queueId", args.Get("queue") ?? ""),
            ("offset", args.Get("offset") ?? ""),
            ("limit", args.Get("limit") ?? ""));
        return ReturnApi(client.Get(path));
    }

    private static int ExecuteSettings(CliArguments args)
    {
        string? rawSub = Positional(args, 1);
        string sub = rawSub?.ToLowerInvariant() ?? "get";
        if (!EnsurePositionals(args, rawSub is null ? 1 : 2, "settings 只接受一个子命令"))
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
            _ => CliOutput.WriteFailure("invalid_arguments", $"未知 settings 子命令：{sub}"),
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
            ? ReturnApi(client.Post("/api/settings/restart"), "服务重启请求已提交")
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
        return ReturnApi(client.Put("/api/settings", body), "设置已更新");
    }

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

    private static int ExecuteMaintenance(CliArguments args)
    {
        string? rawSub = Positional(args, 1);
        string sub = (rawSub ?? "legacy-users").ToLowerInvariant();
        if (!EnsurePositionals(args, rawSub is null ? 1 : 2, "maintenance 只接受一个子命令"))
        {
            return CliExitCodes.For("invalid_arguments");
        }
        var client = new CliApiClient();
        if (sub is "legacy-users" or "list")
        {
            if (!EnsureOptions(args))
            {
                return CliExitCodes.For("invalid_arguments");
            }
            return ReturnApi(client.Get("/api/maintenance/legacy-users"));
        }
        if (sub is not ("prune" or "prune-legacy-user-data"))
        {
            return CliOutput.WriteFailure("invalid_arguments", $"未知 maintenance 子命令：{sub}");
        }
        if (!EnsureOptions(args, "script-id", "user-key"))
        {
            return CliExitCodes.For("invalid_arguments");
        }
        if (!TryRequireOption(args, "script-id", "脚本 ID", out string scriptId, out int error)
            || !TryRequireOption(args, "user-key", "遗留用户键", out string userKey, out error))
        {
            return error;
        }
        string path = "/api/maintenance/legacy-users" + Query(("scriptId", scriptId), ("userKey", userKey));
        return ReturnApi(client.Delete(path), "遗留用户目录已清理");
    }

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

    private static int SendFileMutation(CliApiClient client, CliArguments args, string method, string path, string label)
    {
        if (!EnsureOptions(args, "file"))
        {
            return CliExitCodes.For("invalid_arguments");
        }
        if (!TryReadJsonObject(args, out JsonObject? body, out int error))
        {
            return error;
        }
        CliApiResponse response = method == "POST"
            ? client.Post(path, body)
            : client.Put(path, body);
        return ReturnApi(response, label + "已更新");
    }

    private static int SendIds(CliApiClient client, CliArguments args, string path, string label)
    {
        if (!EnsureOptions(args, "ids"))
        {
            return CliExitCodes.For("invalid_arguments");
        }
        if (!TryRequireOption(args, "ids", label + " ID 列表", out string raw, out int error))
        {
            return error;
        }
        string[] ids = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0 || ids.Any(string.IsNullOrWhiteSpace))
        {
            return CliOutput.WriteFailure("invalid_arguments", "--ids 必须包含逗号分隔的完整 ID 列表");
        }
        var array = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        return ReturnApi(client.Put(path, Object(("ids", array))), label + "顺序已更新");
    }

    private static int ReturnApi(CliApiResponse response, string? message = null)
    {
        return response.Succeeded
            ? WriteApiSuccess(response.Body, message)
            : CliOutput.WriteFailure(response.Code, response.Message, response.Body);
    }

    private static int WriteApiSuccess(JsonNode? body, string? message)
    {
        if (CliOutput.MachineMode)
        {
            CliOutput.WriteSuccess(body, message);
            return 0;
        }
        if (!string.IsNullOrWhiteSpace(message))
        {
            CliOutput.WriteDiagnostic("[完成] " + message);
        }
        if (body is not null)
        {
            Console.WriteLine(body.ToJsonString(NexusPipeline.Utilities.JsonOpts.Indented));
        }
        return 0;
    }

    private static bool TryResolveTarget(
        CliApiClient client,
        string listPath,
        string reference,
        string label,
        out string id,
        out int error)
    {
        id = "";
        CliApiResponse response = client.Get(listPath);
        if (!response.Succeeded)
        {
            error = ReturnApi(response);
            return false;
        }
        if (response.Body is not JsonArray array)
        {
            error = CliOutput.WriteFailure("internal_error", $"服务返回的{label}列表格式无效");
            return false;
        }
        var candidates = array
            .OfType<JsonObject>()
            .Select(item => new CliTarget(
                item["id"]?.ToString() ?? "",
                item["name"]?.ToString() ?? "",
                item))
            .Where(item => item.Id.Length > 0)
            .ToList();
        TargetResolution<CliTarget> resolution = TargetResolver.Resolve(candidates, reference, item => item.Id, item => item.Name);
        if (resolution.IsFound && resolution.Value is not null)
        {
            id = resolution.Value.Id;
            error = 0;
            return true;
        }
        if (resolution.Kind == TargetResolutionKind.Ambiguous)
        {
            var data = new JsonObject
            {
                ["candidates"] = new JsonArray(resolution.Candidates
                    .Select(item => (JsonNode?)new JsonObject { ["id"] = item.Id, ["name"] = item.Name })
                    .ToArray()),
            };
            error = CliOutput.WriteFailure("ambiguous_target", $"{label}名称匹配到多个对象：{reference}", data);
            return false;
        }
        error = CliOutput.WriteFailure("not_found", $"未找到{label}：{reference}");
        return false;
    }

    private static bool TryReadJsonObject(CliArguments args, out JsonObject? objectNode, out int error)
    {
        objectNode = null;
        error = 0;
        if (!TryReadJson(args, out JsonNode? node, out error))
        {
            return false;
        }
        if (node is not JsonObject json)
        {
            error = CliOutput.WriteFailure("validation_error", "--file 内容必须是 JSON 对象");
            return false;
        }
        objectNode = json;
        return true;
    }

    private static bool TryReadJson(CliArguments args, out JsonNode? node, out int error)
    {
        node = null;
        error = 0;
        if (!TryRequireOption(args, "file", "JSON 文件路径（或 -）", out string file, out error))
        {
            return false;
        }
        try
        {
            string text = file == "-"
                ? Console.In.ReadToEnd()
                : File.ReadAllText(file, new UTF8Encoding(false));
            node = JsonNode.Parse(text);
            if (node is null)
            {
                error = CliOutput.WriteFailure("validation_error", "JSON 内容为空");
                return false;
            }
            return true;
        }
        catch (JsonException ex)
        {
            error = CliOutput.WriteFailure("validation_error", $"JSON 内容无效：{ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            error = CliOutput.WriteFailure("validation_error", $"读取 JSON 文件失败：{ex.Message}");
            return false;
        }
    }

    private static bool TryReadFileBytes(CliArguments args, out byte[]? bytes, out string? fileName, out int error)
    {
        bytes = null;
        fileName = null;
        if (!TryRequireOption(args, "file", "文件路径", out string file, out error))
        {
            return false;
        }
        if (file == "-")
        {
            error = CliOutput.WriteFailure("invalid_arguments", "二进制文件参数不支持使用 stdin（--file -）");
            return false;
        }
        try
        {
            fileName = file;
            bytes = File.ReadAllBytes(file);
            error = 0;
            return true;
        }
        catch (Exception ex)
        {
            error = CliOutput.WriteFailure("validation_error", $"读取文件失败：{ex.Message}");
            return false;
        }
    }

    private static bool TryRequirePositional(CliArguments args, int index, string label, out string value, out int error)
    {
        value = "";
        if (args.Positionals.Count <= index || string.IsNullOrWhiteSpace(args.Positionals[index]))
        {
            error = CliOutput.WriteFailure("invalid_arguments", $"缺少{label}");
            return false;
        }
        value = args.Positionals[index];
        error = 0;
        return true;
    }

    private static bool TryRequireOption(CliArguments args, string name, string label, out string value, out int error)
    {
        value = "";
        if (!args.TryGet(name, out string? raw) || raw is null || (name != "secret-value" && string.IsNullOrWhiteSpace(raw)))
        {
            error = CliOutput.WriteFailure("invalid_arguments", $"缺少 --{name}（{label}）");
            return false;
        }
        value = raw;
        error = 0;
        return true;
    }

    private static bool EnsureOptions(CliArguments args, params string[] allowed)
    {
        HashSet<string> accepted = allowed.Select(CliArguments.NormalizeName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string option in args.Options.Keys)
        {
            if (!accepted.Contains(option))
            {
                CliOutput.WriteFailure("invalid_arguments", $"未知选项：--{option}");
                return false;
            }
        }
        return true;
    }

    private static bool EnsurePositionals(CliArguments args, int expected, string message)
    {
        if (args.Positionals.Count == expected)
        {
            return true;
        }
        CliOutput.WriteFailure("invalid_arguments", message);
        return false;
    }

    private static bool TryRequireOption(CliArguments args, string name, string label, out string value, out int error, bool allowEmpty)
    {
        value = "";
        if (!args.TryGet(name, out string? raw) || raw is null || (!allowEmpty && string.IsNullOrWhiteSpace(raw)))
        {
            error = CliOutput.WriteFailure("invalid_arguments", $"缺少 --{name}（{label}）");
            return false;
        }
        value = raw;
        error = 0;
        return true;
    }

    private static string? Positional(CliArguments args, int index)
    {
        return args.Positionals.Count > index ? args.Positionals[index] : null;
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string Query(params (string Key, string Value)[] values)
    {
        string[] parts = values
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => Escape(pair.Key) + "=" + Escape(pair.Value))
            .ToArray();
        return parts.Length == 0 ? "" : "?" + string.Join("&", parts);
    }

    private static JsonObject Object(params (string Name, object? Value)[] values)
    {
        var result = new JsonObject();
        foreach ((string name, object? value) in values)
        {
            result[name] = value switch
            {
                null => null,
                JsonNode node => node,
                _ => JsonSerializer.SerializeToNode(value),
            };
        }
        return result;
    }

    private static string MimeFromExtension(string? extension)
    {
        return extension?.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "",
        };
    }

    private static int WriteUsage()
    {
        const string usage =
            "用法：nexus-pipeline.exe <命令> [子命令] [参数]\n"
            + "\n"
            + "基础：status\n"
            + "资源：script、user、queue、run、history、settings、plugin（含 store/user-settings）、update、maintenance、system-action\n"
            + "\n"
            + "机器接口：所有正式命令支持 --json；复杂对象使用 --file <json|->，--file - 从 stdin 读取。\n"
            + "目标解析：ID 精确优先；名称唯一匹配；同名返回 ambiguous_target。\n"
            + "进程入口：manage、service、web、restart、register、unregister、apply-update。";
        if (CliOutput.MachineMode)
        {
            CliOutput.WriteSuccess(new JsonObject { ["usage"] = usage });
            return 0;
        }
        Console.WriteLine("NexusPipeline 枢链");
        Console.WriteLine(usage);
        return 0;
    }

    private sealed record CliTarget(string Id, string Name, JsonObject Data);
}

internal static class CliRouterIntExtensions
{
    public static int AlsoWriteUsage(this int result)
    {
        if (!CliOutput.MachineMode)
        {
            Console.WriteLine("使用 nexus-pipeline.exe --help 查看命令帮助。");
        }
        return result;
    }
}
