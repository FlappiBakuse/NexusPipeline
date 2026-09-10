using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.App;
using NexusPipeline.Localization;

namespace NexusPipeline.Cli;

internal static partial class CliCommandRouter
{
    private static int ExecuteRun(CliArguments args)
    {
        string? rawSub = Positional(args, 1);
        string? sub = rawSub?.ToLowerInvariant();
        int error;
        if (sub is null)
        {
            return CliOutput.WriteFailure(
                "invalid_arguments",
                CliText.Get("error.missing_subcommand", "缺少 {command} 子命令（{commands}）",
                    ("command", "run"),
                    ("commands", "script/queue/get/list/cancel")));
        }
        if (sub == "get")
        {
            if (!EnsurePositionals(args, 3, CliText.Get("error.requires_value", "{usage}需要{label}",
                    ("usage", "run get"),
                    ("label", CliText.Label("运行 ID"))))
                || !EnsureOptions(args))
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
            if (!EnsurePositionals(args, 2, CliText.Get("error.extra_arguments", "{usage} 不接受额外参数", ("usage", "run list")))
                || !EnsureOptions(args))
            {
                return CliExitCodes.For("invalid_arguments");
            }
            return ReturnApi(new CliApiClient().Get("/api/runs"));
        }
        if (sub == "cancel")
        {
            if (!EnsurePositionals(args, 3, CliText.Get("error.requires_value", "{usage}需要{label}",
                    ("usage", "run cancel"),
                    ("label", CliText.Label("运行 ID"))))
                || !EnsureOptions(args))
            {
                return CliExitCodes.For("invalid_arguments");
            }
            return ExecuteCancel(args);
        }
        if (sub is not ("script" or "queue"))
        {
            return CliOutput.WriteFailure(
                "invalid_arguments",
                CliText.Get("error.unknown_subcommand", "未知 {command} 子命令：{subcommand}",
                    ("command", "run"),
                    ("subcommand", sub)));
        }

        int targetPosition = 2;
        if (!EnsurePositionals(args, targetPosition + 1, CliText.Get("error.requires_target", "{usage}需要一个目标", ("usage", $"run {sub}")))
            || !EnsureOptions(args, "mode", "auto", "manual", "user", "detach", "dry-run"))
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
        if (args.Has("dry-run"))
        {
            if (args.Has("detach") || args.Has("auto") || args.Has("manual") || args.Has("mode"))
            {
                return CliOutput.WriteFailure(
                    "invalid_arguments",
                    CliText.Get("error.dry_run_options", "dry-run 仅支持目标和可选 --user"));
            }
            JsonObject explainBody = sub == "script"
                ? Object(("scriptId", id), ("userName", args.Get("user") ?? ""))
                : Object(("queueId", id));
            return ReturnApi(client.Post($"/api/dispatch/explain/{sub}", explainBody));
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
            return CliOutput.WriteFailure(
                "internal_error",
                CliText.Get("error.run_id_missing", "服务已接受任务，但响应中没有有效 runId"));
        }
        if (args.Has("detach"))
        {
            return ReturnApi(response, CliText.Get("success.task_submitted", "任务已提交（detach）"));
        }
        return PollRun(client, dispatch["runId"]!.ToString());
    }

    private static int ExecuteCancel(CliArguments args)
    {
        if (!EnsurePositionals(args, 3, CliText.Get("error.requires_value", "{usage}需要{label}",
                ("usage", "run cancel"),
                ("label", CliText.Label("运行 ID"))))
            || !EnsureOptions(args))
        {
            return CliExitCodes.For("invalid_arguments");
        }
        if (!TryRequirePositional(args, 2, "运行 ID", out string runId, out int error))
        {
            return error;
        }
        return ReturnApi(
            new CliApiClient().Post("/api/cancel", Object(("runId", runId))),
            CliText.Get("success.cancel_requested", "已发送取消请求"));
    }

    private static int PollRun(CliApiClient client, string runId)
    {
        int timeoutSeconds = 6 * 60 * 60;
        if (client is null)
        {
            return CliOutput.WriteFailure(
                "service_unavailable",
                CliText.Get("error.service_unavailable", "无法连接到常驻服务"));
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
                CliOutput.WriteProgress(CliText.Get(
                    "progress.run_status",
                    "运行 {runId}：{status}",
                    ("runId", runId),
                    ("status", currentStatus)));
            }
            if (!status.Equals("running", StringComparison.OrdinalIgnoreCase))
            {
                static string Status(JsonNode? record) => record?["status"]?.ToString() ?? "";
                bool cancelled = body?["records"] is JsonArray records
                    && records.Any(record => Status(record).Equals("cancelled", StringComparison.OrdinalIgnoreCase));
                bool failed = body?["records"] is JsonArray failedRecords
                    && failedRecords.Any(record => Status(record) is not ("success" or "skipped"));
                if (cancelled)
                {
                    return CliOutput.WriteFailure(
                        "cancelled",
                        CliText.Get("status.run_cancelled", "运行已取消"),
                        body);
                }
                if (failed)
                {
                    return CliOutput.WriteFailure(
                        "execution_failed",
                        CliText.Get("status.run_failed_records", "运行完成，但存在失败记录"),
                        body);
                }
                CliOutput.WriteSuccess(body, CliText.Get("success.run_completed", "运行已完成"));
                return 0;
            }
            Thread.Sleep(1000);
        }
        return CliOutput.WriteFailure(
            "timeout",
            CliText.Get("error.run_poll_timeout", "轮询运行结果超过 6 小时上限"),
            Object(("runId", runId)));
    }

}
