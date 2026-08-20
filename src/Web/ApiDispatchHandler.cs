using System.Net;
using System.Text.Json.Nodes;
using NexusPipeline.App.Commands;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;

namespace NexusPipeline.Web;

[ApiRoute("dispatch")]
internal static class ApiDispatchHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg, string body)
    {
        // GET /api/dispatch/{runId}：查询运行任务（含已结束，CLI 轮询结果用）；seg[1] 非 script/queue 即视为 runId。
        if (method == "GET" && seg.Length == 2
            && !seg[1].Equals("script", StringComparison.OrdinalIgnoreCase)
            && !seg[1].Equals("queue", StringComparison.OrdinalIgnoreCase))
        {
            await HandleQueryAsync(context, seg[1]).ConfigureAwait(false);
            return;
        }
        if (method != "POST")
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        JsonNode? node = HttpHelper.ParseBody(body);
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
                RunningExecution exec = RuntimeContext.Instance.Commands.StartScript(scriptId, mode, Audit.Web, userName);
                await HttpHelper.WriteJsonAsync(context, new { runId = exec.Id, ok = true }).ConfigureAwait(false);
                return;
            }
            if (seg.Length >= 2 && seg[1].ToLowerInvariant() == "queue")
            {
                string queueId = node.Get("queueId").Str();
                DispatchQueue? queue = RuntimeContext.Instance.FindQueue(queueId);
                if (queue is null)
                {
                    throw new InvalidOperationException($"调度队列不存在：{queueId}");
                }
                string? blocked = DispatchCenter.QueueBlockedBy(queue);
                if (blocked is not null)
                {
                    throw new InvalidOperationException($"队列「{queue.Name}」引用的脚本「{blocked}」正在运行，请先退出后再执行");
                }
                RunningExecution exec = RuntimeContext.Instance.Commands.StartQueue(queueId, mode, Audit.Web);
                await HttpHelper.WriteJsonAsync(context, new { runId = exec.Id, ok = true }).ConfigureAwait(false);
                return;
            }
            await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await HttpHelper.WriteJsonAsync(context, new { ok = false, error = ex.Message }, 400).ConfigureAwait(false);
        }
    }

    /// <summary>查询运行任务（含已结束）：返回状态快照与完整记录列表；不存在返回 404。</summary>
    private static async Task HandleQueryAsync(HttpListenerContext context, string runId)
    {
        RunningExecution? exec = RuntimeContext.Instance.Center.FindAny(runId);
        if (exec is null)
        {
            await HttpHelper.WriteJsonAsync(context, new { error = $"未找到运行任务：{runId}" }, 404).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, new
        {
            exec.Id,
            exec.Kind,
            exec.TargetId,
            exec.TargetName,
            exec.Mode,
            exec.Status,
            exec.StartedAt,
            exec.FinishedAt,
            exec.TotalTasks,
            exec.DoneTasks,
            exec.CurrentScriptName,
            exec.CurrentStatus,
            exec.CurrentAttempt,
            exec.CurrentMaxAttempts,
            logTail = exec.LogTail(60),
            records = exec.SnapshotRecords(),
        }).ConfigureAwait(false);
    }

    [ApiRoute("cancel")]
    public static async Task HandleCancel(HttpListenerContext context, string method, string body)
    {
        if (method != "POST")
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        JsonNode? node = HttpHelper.ParseBody(body);
        string runId = node.Get("runId").Str();
        try
        {
            RuntimeContext.Instance.Commands.Cancel(runId, Audit.Web);
            await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await HttpHelper.WriteJsonAsync(context, new { ok = false, error = ex.Message }, 400).ConfigureAwait(false);
        }
    }
}
