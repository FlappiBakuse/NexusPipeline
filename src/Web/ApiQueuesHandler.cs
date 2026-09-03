using System.Net;
using System.Text.Json.Nodes;
using NexusPipeline.App.Commands;
using NexusPipeline.App.Contracts;
using NexusPipeline.App.Queries;
using NexusPipeline.Models;
using NexusPipeline.Services;

namespace NexusPipeline.Web;

[ApiRoute("queues")]
internal static class ApiQueuesHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg, string body)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        QueueQueries queries = ctx.Resolve<QueueQueries>();
        if (method == "GET" && seg.Length == 1)
        {
            IReadOnlyList<QueueReadModel> snapshot = queries.List();
            Audit.Log(Audit.Web, "查询调度队列列表", $"{snapshot.Count} 条");
            var result = snapshot.Select(ProjectQueue).ToList();
            await HttpHelper.WriteJsonAsync(context, result).ConfigureAwait(false);
            return;
        }
        if (method == "GET" && seg.Length == 2
            && !seg[1].Equals("order", StringComparison.OrdinalIgnoreCase))
        {
            QueueReadModel? queue = queries.Find(Uri.UnescapeDataString(seg[1]));
            if (queue is null)
            {
                await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
                return;
            }
            await HttpHelper.WriteJsonAsync(context, ProjectQueue(queue)).ConfigureAwait(false);
            return;
        }
        if (method == "PUT" && seg.Length == 2 && seg[1].Equals("order", StringComparison.OrdinalIgnoreCase))
        {
            await HandleReorderQueuesAsync(context, body).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && seg.Length == 1)
        {
            DispatchQueue? queue = HttpHelper.ParseBody<DispatchQueue>(body);
            if (queue is null || string.IsNullOrWhiteSpace(queue.Name))
            {
                await HttpHelper.WriteJsonAsync(context, new { error = "队列名称不能为空" }, 400).ConfigureAwait(false);
                return;
            }
            OperationResult<DispatchQueue> result = QueueCommands.Create(queue);
            if (!result.Succeeded)
            {
                await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
                return;
            }
            await HttpHelper.WriteJsonAsync(context, result.Value!).ConfigureAwait(false);
            return;
        }
        if (method == "PUT" && seg.Length == 2)
        {
            DispatchQueue? update = HttpHelper.ParseBody<DispatchQueue>(body);
            if (update is null)
            {
                await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
                return;
            }
            OperationResult<DispatchQueue> result = QueueCommands.Update(seg[1], update);
            if (!result.Succeeded)
            {
                await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
                return;
            }
            await HttpHelper.WriteJsonAsync(context, result.Value!).ConfigureAwait(false);
            return;
        }
        if (method == "DELETE" && seg.Length == 2)
        {
            OperationResult<DispatchQueue?> result = QueueCommands.Delete(seg[1]);
            if (!result.Succeeded)
            {
                await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
                return;
            }
            await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
            return;
        }
        await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
    }

    /// <summary>队列顺序重排：请求体携带完整 id 名单，与现有集合完全一致时按新顺序重赋 Index 落盘。</summary>
    private static async Task HandleReorderQueuesAsync(HttpListenerContext context, string body)
    {
        JsonNode? node = HttpHelper.ParseBody(body);
        List<string>? ids = node?["ids"] is JsonArray array
            ? array.Select(item => item?.ToString() ?? "").ToList()
            : null;
        OperationResult<bool> result = QueueCommands.Reorder(ids);
        if (!result.Succeeded)
        {
            await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
    }

    private static object ProjectQueue(QueueReadModel model)
    {
        DispatchQueue queue = model.Queue;
        return new
        {
            queue.Id,
            queue.Name,
            queue.AutoRunMode,
            queue.CompletionAction,
            queue.TimeSets,
            queue.Tasks,
            queue.NotifyEnabled,
            nextTrigger = model.NextTrigger,
        };
    }
}
