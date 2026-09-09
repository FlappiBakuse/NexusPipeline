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
                return CliOutput.WriteFailure("invalid_arguments", "dry-run 仅支持目标和可选 --user");
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
                static string Status(JsonNode? record) => record?["status"]?.ToString() ?? "";
                bool cancelled = body?["records"] is JsonArray records
                    && records.Any(record => Status(record).Equals("cancelled", StringComparison.OrdinalIgnoreCase));
                bool failed = body?["records"] is JsonArray failedRecords
                    && failedRecords.Any(record => Status(record) is not ("success" or "skipped"));
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

}
