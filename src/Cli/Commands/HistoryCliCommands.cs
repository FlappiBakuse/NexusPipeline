using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.App;
using NexusPipeline.Localization;

namespace NexusPipeline.Cli;

internal static partial class CliCommandRouter
{
    private static int ExecuteHistory(CliArguments args)
    {
        string? rawSub = Positional(args, 1);
        string? sub = rawSub?.ToLowerInvariant();
        var client = new CliApiClient();
        if (sub is "dates")
        {
            if (!EnsurePositionals(args, 2, CliText.Get("error.extra_arguments", "{usage} 不接受额外参数", ("usage", "history dates")))
                || !EnsureOptions(args, "days"))
            {
                return CliExitCodes.For("invalid_arguments");
            }
            string query = Query(("days", args.Get("days") ?? "3"));
            return ReturnApi(client.Get("/api/history/dates" + query));
        }
        if (sub is "get" or "detail")
        {
            if (!EnsurePositionals(
                    args,
                    3,
                    CliText.Get(
                        "error.requires_value",
                        "{usage}需要{label}",
                        ("usage", "history get"),
                        ("label", CliText.Label("历史记录 ID"))))
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
            return CliOutput.WriteFailure(
                "invalid_arguments",
                CliText.Get("error.unknown_subcommand", "未知 {command} 子命令：{subcommand}",
                    ("command", "history"),
                    ("subcommand", sub)));
        }
        if (!EnsurePositionals(
                args,
                rawSub is null ? 1 : 2,
                CliText.Get("error.extra_arguments", "{usage} 不接受额外参数", ("usage", "history list")))
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

}
