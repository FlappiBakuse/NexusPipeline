using System.Net;
using System.Text.Json.Nodes;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;

namespace NexusPipeline.Web;

[ApiRoute("dispatch")]
internal static class ApiDispatchHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg, string body)
    {
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
                RunningExecution exec = RuntimeContext.Instance.Center.StartScript(scriptId, mode, Audit.Web, userName);
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
                RunningExecution exec = RuntimeContext.Instance.Center.StartQueue(queueId, mode, Audit.Web);
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
            RuntimeContext.Instance.Center.Cancel(runId, Audit.Web);
            await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await HttpHelper.WriteJsonAsync(context, new { ok = false, error = ex.Message }, 400).ConfigureAwait(false);
        }
    }
}
