using System.Net;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Serialization;

namespace NexusPipeline.ControlPlane.Http;

[ApiRoute("dispatch")]
internal static class ApiDispatchHandler
{
    public static async Task Handle(
        HttpListenerContext context,
        string method,
        string[] seg,
        string body,
        ExecutionDispatcher dispatchCenter,
        ExecutionExplainService executionExplain)
    {
        // GET /api/dispatch/{runId}：查询运行任务（含已结束，CLI 轮询结果用）；seg[1] 非 script/queue 即视为 runId。
        if (method == "GET" && seg.Length == 2
            && !seg[1].Equals("script", StringComparison.OrdinalIgnoreCase)
            && !seg[1].Equals("queue", StringComparison.OrdinalIgnoreCase))
        {
            await HandleQueryAsync(context, seg[1], dispatchCenter).ConfigureAwait(false);
            return;
        }
        if (method != "POST")
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        JsonNode? node = HttpHelper.ParseBody(body);
        if (seg.Length >= 3
            && seg[1].Equals("explain", StringComparison.OrdinalIgnoreCase))
        {
            await HandleExplainAsync(context, seg[2], node, executionExplain).ConfigureAwait(false);
            return;
        }
        string mode = node.Get("mode").Str();
        if (mode != "auto")
        {
            mode = "manual";
        }
        try
        {
            if (seg.Length >= 2 && seg[1].ToLowerInvariant() == "script")
            {
                string scriptId = node.Get("scriptId").Str();
                string userName = node.Get("userName").Str();
                RunningExecution exec = dispatchCenter.StartScript(scriptId, mode, Audit.Web, userName);
                await HttpHelper.WriteJsonAsync(context, new { runId = exec.Id, ok = true }).ConfigureAwait(false);
                return;
            }
            if (seg.Length >= 2 && seg[1].ToLowerInvariant() == "queue")
            {
                string queueId = node.Get("queueId").Str();
                RunningExecution exec = dispatchCenter.StartQueue(queueId, mode, Audit.Web);
                await HttpHelper.WriteJsonAsync(context, new { runId = exec.Id, ok = true }).ConfigureAwait(false);
                return;
            }
            await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
        }
        catch (ExecutionAdmissionException admission)
        {
            await ExecutionConflictResponse.WriteAdmissionAsync(context, admission).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[调度] 启动运行失败：{ex}");
            await HttpHelper.ErrorAsync(context, "dispatch_failed", 400).ConfigureAwait(false);
        }
    }

    private static async Task HandleExplainAsync(
        HttpListenerContext context,
        string kind,
        JsonNode? node,
        ExecutionExplainService service)
    {
        try
        {
            ExecutionExplainResult result;
            if (kind.Equals("script", StringComparison.OrdinalIgnoreCase))
            {
                string scriptId = node.Get("scriptId").Str();
                string? userName = node.Get("userName").Str();
                result = service.ExplainScript(scriptId, string.IsNullOrWhiteSpace(userName) ? null : userName);
            }
            else if (kind.Equals("queue", StringComparison.OrdinalIgnoreCase))
            {
                result = service.ExplainQueue(node.Get("queueId").Str());
            }
            else
            {
                await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
                return;
            }
            await HttpHelper.WriteJsonAsync(context, new { ok = true, result }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[调度] 运行计划检查失败：{ex}");
            await HttpHelper.ErrorAsync(context, "explain_failed", 400).ConfigureAwait(false);
        }
    }

    /// <summary>查询运行任务（含已结束）：返回状态快照与完整记录列表；不存在返回 404。</summary>
    private static async Task HandleQueryAsync(
        HttpListenerContext context,
        string runId,
        ExecutionDispatcher dispatchCenter)
    {
        RunningExecution? exec = dispatchCenter.FindAny(runId);
        if (exec is null)
        {
            await HttpHelper.ErrorAsync(context, "run_not_found", 404, new { runId }).ConfigureAwait(false);
            return;
        }
        RunningExecutionSnapshot snapshot = exec.Snapshot();
        await HttpHelper.WriteJsonAsync(context, new
        {
            snapshot.Id,
            snapshot.Kind,
            snapshot.TargetId,
            snapshot.TargetName,
            snapshot.Mode,
            snapshot.Status,
            snapshot.StartedAt,
            snapshot.FinishedAt,
            snapshot.TotalTasks,
            snapshot.DoneTasks,
            snapshot.CurrentScriptName,
            snapshot.CurrentScriptId,
            snapshot.CurrentStatus,
            snapshot.CurrentAttempt,
            snapshot.CurrentMaxAttempts,
            persistenceWarning = snapshot.PersistenceWarning,
            logTail = snapshot.LogTail,
            logSegmentId = snapshot.LogSegmentId,
            logSegmentSequence = snapshot.LogSegmentSequence,
            logSegment = snapshot.LogSegment,
            cancelRequested = snapshot.CancelRequested,
            cancellationPhase = snapshot.CancellationPhase,
            cancellationTimingMs = snapshot.CancellationTimingMs,
            logEntries = snapshot.LogEntries.Select(entry => new
            {
                sequence = entry.Sequence,
                logSegmentId = entry.LogSegmentId,
                timestamp = entry.Timestamp,
                level = entry.Level.ToString().ToLowerInvariant(),
                text = entry.FormattedText,
            }).ToArray(),
            records = snapshot.Records,
        }).ConfigureAwait(false);
    }

    [ApiRoute("cancel")]
    public static async Task HandleCancel(
        HttpListenerContext context,
        string method,
        string body,
        ExecutionDispatcher dispatchCenter)
    {
        if (method != "POST")
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        long handlerTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        DateTime requestReceivedUtc = DateTime.UtcNow;
        JsonNode? node = HttpHelper.ParseBody(body);
        string runId = node.Get("runId").Str();
        try
        {
            CancellationRequestResult result = dispatchCenter.RequestCancellation(runId, Audit.Web);
            double requestMs = System.Diagnostics.Stopwatch.GetElapsedTime(context.AcceptedTimestamp, handlerTimestamp).TotalMilliseconds;
            double cancelMs = System.Diagnostics.Stopwatch.GetElapsedTime(handlerTimestamp).TotalMilliseconds;
            context.Response.Headers["Server-Timing"] = FormattableString.Invariant($"request;dur={requestMs:F3}, cancel;dur={cancelMs:F3}, received;desc=\"{requestReceivedUtc:O}\"");
            string state = result switch
            {
                CancellationRequestResult.Accepted => "accepted",
                CancellationRequestResult.AlreadyRequested => "already_requested",
                _ => "already_finished",
            };
            await HttpHelper.WriteJsonAsync(context, new { ok = true, cancellation = state }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[调度] 取消运行失败：{ex}");
            await HttpHelper.ErrorAsync(context, "dispatch_cancel_failed", 400).ConfigureAwait(false);
        }
    }
}
