using System.Text.Json.Nodes;
using NexusPipeline.ControlPlane.Cli;

namespace NexusPipeline.ControlPlane.Cli.Commands;

internal static partial class CliCommandRouter
{
    private static int? ExecuteExtended(CliArguments args)
    {
        string command = Positional(args, 0)?.ToLowerInvariant() ?? "";
        string sub = Positional(args, 1)?.ToLowerInvariant() ?? "";
        var client = new CliApiClient();
        switch (command, sub)
        {
            case ("provider-config", _): return ExecuteProviderConfig(args, client);
            case ("dashboard", _): return ExecuteDashboard(args, client);
            case ("limits", _): return ReadEndpoint(args, client, 1, "/api/limits");
            case ("script", "edit-sessions"): return ReadEndpoint(args, client, 2, "/api/scripts/edit-sessions");
            case ("user", "task-summaries"): return ReadEndpoint(args, client, 2, "/api/users/task-summaries");
            case ("user", "badges"): return ReadEndpoint(args, client, 2, "/api/plugin-contributions/user-list-badges");
            case ("settings", "access-token"): return ReadEndpoint(args, client, 2, "/api/settings/access-token");
            case ("plugin", "frontend"): return ReadEndpoint(args, client, 2, "/api/plugin-runtime/frontend");
            case ("run", "tasks"):
                if (!Valid(args, 3)) return Invalid();
                return ReturnApi(client.Get($"/api/runs/{Escape(args.Positionals[2])}/tasks"));
            case ("history", "summary"):
            case ("history", "users"):
                if (!Valid(args, 2, "days", "date", "from", "to", "script", "queue", "user-key", "status")) return Invalid();
                return ReturnApi(client.Get($"/api/history/{sub}" + HistoryQuery(args)));
            case ("plugin", "store") when Positional(args, 2) is "get" or "update-all":
                bool get = Positional(args, 2) == "get";
                if (!Valid(args, get ? 4 : 3)) return Invalid();
                return ReturnApi(get ? client.Get($"/api/plugins/store/{Escape(args.Positionals[3])}/detail")
                    : client.Post("/api/plugins/store/update-all"));
            case ("user", "binding") when Positional(args, 2) is "reorder" or "task-plan" or "repair-preview" or "repair" or "config-status":
                return ExecuteBindingExtension(args, client);
            case ("plugin", "ui"): return ExecutePluginUi(args, client);
            case ("plugin", "request"): return ExecutePluginRequest(args, client);
            case ("client-session", _): return ExecuteClientSession(args, client);
            case ("script", "type-icon"):
                if (!Valid(args, 3, "output") || !TryRequireOption(args, "output", "新输出文件", out string iconOutput, out _)) return Invalid();
                return ReturnApi(client.Download($"/api/scripts/type-icons/{Escape(args.Positionals[2])}", iconOutput));
            case ("history", "image"):
                if (!Valid(args, 3, "attempt", "screenshot", "output")
                    || !TryRequireOption(args, "attempt", "尝试序号", out string attempt, out _)
                    || !TryRequireOption(args, "screenshot", "截图 ID", out string screenshot, out _)
                    || !TryRequireOption(args, "output", "新输出文件", out string imageOutput, out _)) return Invalid();
                return ReturnApi(client.Download("/api/history/image" + Query(("id", args.Positionals[2]), ("attempt", attempt), ("screenshot", screenshot)), imageOutput));
            case ("run", "preview"):
                if (!Valid(args, 3, "plugin", "output")
                    || !TryRequireOption(args, "plugin", "已启用的预览插件", out string plugin, out _)
                    || !TryRequireOption(args, "output", "新输出文件", out string previewOutput, out _)) return Invalid();
                return ReturnApi(client.Download($"/api/execution-preview/{Escape(args.Positionals[2])}" + Query(("plugin", plugin)), previewOutput));
            case ("fs", "browse"):
                if (!Valid(args, 2, "path")) return Invalid();
                return ReturnApi(client.Get("/api/fs/browse" + Query(("path", args.Get("path") ?? ""))));
            case ("native-dialog", _):
                if (!Valid(args, 1, "file")) return Invalid();
                return SendFileMutation(client, args, "POST", "/api/native-dialog", "本机路径选择");
            default: return null;
        }
    }

    private static bool Valid(CliArguments args, int count, params string[] options) =>
        EnsurePositionals(args, count, "命令参数数量不正确；请查看 help") && EnsureOptions(args, options);

    private static int Invalid() => CliExitCodes.For("invalid_arguments");

    private static int BadCommand() => CliOutput.WriteFailure("invalid_arguments", "命令或选项组合无效；请查看 help");

    private static int ReadEndpoint(CliArguments args, CliApiClient client, int count, string path) =>
        Valid(args, count) ? ReturnApi(client.Get(path)) : Invalid();

    private static string HistoryQuery(CliArguments args) => Query(
        ("date", args.Get("date") ?? ""), ("days", args.Get("days") ?? "3"),
        ("from", args.Get("from") ?? ""), ("to", args.Get("to") ?? ""),
        ("scriptId", args.Get("script") ?? ""), ("queueId", args.Get("queue") ?? ""),
        ("userKey", args.Get("user-key") ?? ""), ("status", args.Get("status") ?? ""),
        ("offset", args.Get("offset") ?? ""), ("limit", args.Get("limit") ?? ""));

    private static int ExecuteProviderConfig(CliArguments args, CliApiClient client)
    {
        string? action = Positional(args, 1);
        if (action == "list") return ReadEndpoint(args, client, 2, "/api/provider-configurations");
        if (action == "start")
        {
            if (!Valid(args, 2, "file")) return Invalid();
            return SendFileMutation(client, args, "POST", "/api/provider-configurations", "Provider 配置");
        }
        if (action is not ("get" or "cancel")) return BadCommand();
        if (!Valid(args, 3)) return Invalid();
        string path = $"/api/provider-configurations/{Escape(args.Positionals[2])}";
        return ReturnApi(action == "get" ? client.Get(path) : client.Post(path + "/cancel"));
    }

    private static int ExecuteDashboard(CliArguments args, CliApiClient client)
    {
        string? action = Positional(args, 1);
        if (action == "cards") return ReadEndpoint(args, client, 2, "/api/dashboard/cards");
        if (action == "get")
        {
            if (!Valid(args, 2)) return Invalid();
            var response = client.Get("/api/dashboard/layout");
            return response.Succeeded ? WriteApiSuccess(Object(("etag", client.ETag), ("snapshot", response.Body)), null) : ReturnApi(response);
        }
        if (action != "update") return BadCommand();
        if (!Valid(args, 2, "file", "etag")) return Invalid();
        if (!TryRequireOption(args, "etag", "dashboard get 返回的 ETag", out string etag, out int error)
            || !TryReadJsonObject(args, out JsonObject? body, out error)) return error;
        return ReturnApi(client.Send("PUT", "/api/dashboard/layout", body, new Dictionary<string, string> { ["If-Match"] = etag }));
    }

    private static int ExecuteBindingExtension(CliArguments args, CliApiClient client)
    {
        string action = args.Positionals[2];
        if (!Valid(args, action == "reorder" ? 4 : 5,
                action == "reorder" ? ["ids"] : action == "repair" ? ["file"] : [])) return Invalid();
        if (!TryResolveTarget(client, "/api/users", args.Positionals[3], "用户", out string userId, out int error)) return error;
        string path = $"/api/users/{Escape(userId)}/bindings";
        if (action == "reorder") return SendIds(client, args, path + "/order", "用户绑定");
        if (!TryResolveTarget(client, "/api/scripts", args.Positionals[4], "脚本实例", out string scriptId, out error)) return error;
        path += $"/{Escape(scriptId)}/" + (action == "task-plan" ? "task-plan" : action == "config-status" ? "edit-config" : "config-repair");
        return action == "repair" ? SendFileMutation(client, args, "POST", path, "配置修复") : ReturnApi(client.Get(path));
    }

    private static int ExecutePluginUi(CliArguments args, CliApiClient client)
    {
        string? action = Positional(args, 2);
        int count = action == "query" ? 3 : action == "save" ? 5 : action == "action" ? 6 : 0;
        if (count == 0) return BadCommand();
        if (!Valid(args, count, "file")) return Invalid();
        string path = action == "query" ? "/api/plugin-contributions/ui/query"
            : $"/api/plugin-contributions/ui/{Escape(args.Positionals[3])}/{Escape(args.Positionals[4])}";
        if (action == "action") path += $"/action/{Escape(args.Positionals[5])}";
        return SendFileMutation(client, args, action == "save" ? "PUT" : "POST", path, "插件贡献");
    }

    private static int ExecutePluginRequest(CliArguments args, CliApiClient client)
    {
        if (!Valid(args, 5, "file", "output", "binary", "content-type", "session-file")) return Invalid();
        string method = args.Positionals[3].ToUpperInvariant();
        string relative = args.Positionals[4];
        if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE")
            || relative.Contains('#') || relative.Contains('\\') || relative.StartsWith('/')
            || !args.Positionals[2].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            || relative.Split('?')[0].Split('/').Any(part => string.IsNullOrEmpty(part) || Uri.UnescapeDataString(part) is "." or ".." || Uri.UnescapeDataString(part).IndexOfAny(['/', '\\', '%']) >= 0))
            return CliOutput.WriteFailure("invalid_arguments", "插件路径必须是插件内相对路径，不允许跳转或路径穿越");
        string path = $"/api/plugin-api/{Escape(args.Positionals[2])}/{relative}";
        if (args.Has("output"))
        {
            if (method != "GET" || args.Has("file") || args.Has("binary") || args.Has("session-file")) return BadCommand();
            if (!TryRequireOption(args, "output", "新输出文件", out string output, out _)) return Invalid();
            return ReturnApi(client.Download(path, output));
        }
        var headers = ReadSessionHeaders(args);
        if (args.Has("binary"))
        {
            if (method is not ("POST" or "PUT" or "PATCH") || args.Has("file")) return BadCommand();
            if (!TryRequireOption(args, "binary", "二进制文件", out string binary, out _)
                || !TryRequireOption(args, "content-type", "内容类型", out string contentType, out _)) return Invalid();
            byte[] bytes = NexusPipeline.Platform.Storage.LocalFileReader.ReadAllBytes(binary);
            if (bytes.Length > 16 * 1024 * 1024) return CliOutput.WriteFailure("validation_error", "插件请求超过 16 MiB");
            return ReturnApi(client.Send(method, path, null, headers, bytes, contentType));
        }
        if (args.Has("content-type")) return BadCommand();
        JsonObject? body = null;
        if (args.Has("file") && !TryReadJsonObject(args, out body, out int error)) return error;
        if (method == "GET" && body is not null) return BadCommand();
        return ReturnApi(client.Send(method, path, body, headers));
    }

    private static IReadOnlyDictionary<string, string>? ReadSessionHeaders(CliArguments args)
    {
        if (!args.Has("session-file")) return null;
        string text = NexusPipeline.Platform.Storage.LocalFileReader.ReadAllText(args.Get("session-file") ?? "", System.Text.Encoding.UTF8);
        var session = JsonNode.Parse(text);
        string? token = session?["token"]?.GetValue<string>() ?? session?["data"]?["token"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl)) throw new InvalidDataException("客户端会话文件无效");
        return new Dictionary<string, string> { ["X-Nxp-Client-Session"] = token };
    }

    private static int ExecuteClientSession(CliArguments args, CliApiClient client)
    {
        string? action = Positional(args, 1);
        if (action == "create")
            return Valid(args, 2) ? ReturnApi(client.Post("/api/client-sessions", new JsonObject())) : Invalid();
        if (action is not ("get" or "revoke")) return BadCommand();
        if (!Valid(args, 2, "session-file") || !TryRequireOption(args, "session-file", "会话文件", out _, out _)) return Invalid();
        return ReturnApi(client.Send(action == "get" ? "GET" : "DELETE", "/api/client-sessions/current", null, ReadSessionHeaders(args)));
    }
}
