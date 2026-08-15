using System.Net;
using System.Text.Json.Nodes;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;

namespace NexusPipeline.Web;

[ApiRoute("queues")]
internal static class ApiQueuesHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg, string body)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        if (method == "GET" && seg.Length == 1)
        {
            Audit.Log(Audit.Web, "查询调度队列列表", $"{ctx.Queues.Count} 条");
            var result = ctx.Queues.OrderBy(queue => queue.Index).Select(queue => new
            {
                queue.Id,
                queue.Name,
                queue.AutoRunMode,
                queue.CompletionAction,
                queue.TimeSets,
                queue.Tasks,
                queue.NotifyEnabled,
                nextTrigger = RuntimeContext.Instance.Scheduler.NextTriggerFor(queue),
            }).ToList();
            await HttpHelper.WriteJsonAsync(context, result).ConfigureAwait(false);
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
            string? limitError = Limits.CheckQueueCount(ctx.Queues.Count)
                ?? Limits.CheckNameBytes(queue.Name, Limits.Current.MaxQueueNameBytes, "队列名称")
                ?? Limits.CheckTimeSets(queue.TimeSets.Count)
                ?? CheckTimeFormat(queue)
                ?? Limits.CheckQueueTotalUsers(Limits.QueueTotalUsers(ctx, queue))
                ?? Limits.CheckQueueMix(ctx, queue);
            if (limitError is not null)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = limitError }, 400).ConfigureAwait(false);
                return;
            }
            if (string.IsNullOrWhiteSpace(queue.Id) || ctx.FindQueue(queue.Id) is null)
            {
                queue.Id = Guid.NewGuid().ToString("N");
            }
            if (ctx.Queues.Count > 0)
            {
                queue.Index = ctx.Queues.Max(item => item.Index) + 1;
            }
            NormalizeQueue(queue);
            ctx.Queues.Add(queue);
            DataStore.SaveQueues(ctx.Queues);
            Audit.Log(Audit.Web, "添加调度队列", $"{queue.Name}（id={queue.Id}，任务 {queue.Tasks.Count} 项）");
            await HttpHelper.WriteJsonAsync(context, queue).ConfigureAwait(false);
            return;
        }
        if (method == "PUT" && seg.Length == 2)
        {
            DispatchQueue? update = HttpHelper.ParseBody<DispatchQueue>(body);
            DispatchQueue? existing = ctx.FindQueue(seg[1]);
            if (update is null || existing is null)
            {
                await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
                return;
            }
            string? limitError = Limits.CheckNameBytes(update.Name, Limits.Current.MaxQueueNameBytes, "队列名称")
                ?? Limits.CheckTimeSets(update.TimeSets.Count)
                ?? CheckTimeFormat(update)
                ?? Limits.CheckQueueTotalUsers(Limits.QueueTotalUsers(ctx, update))
                ?? Limits.CheckQueueMix(ctx, update);
            if (limitError is not null)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = limitError }, 400).ConfigureAwait(false);
                return;
            }
            update.Id = existing.Id;
            update.Index = existing.Index;
            NormalizeQueue(update);
            int index = ctx.Queues.IndexOf(existing);
            ctx.Queues[index] = update;
            DataStore.SaveQueues(ctx.Queues);
            Audit.Log(Audit.Web, "修改调度队列", $"{update.Name}（id={update.Id}，任务 {update.Tasks.Count} 项）");
            await HttpHelper.WriteJsonAsync(context, update).ConfigureAwait(false);
            return;
        }
        if (method == "DELETE" && seg.Length == 2)
        {
            DispatchQueue? removed = ctx.FindQueue(seg[1]);
            ctx.Queues.RemoveAll(queue => queue.Id == seg[1]);
            DataStore.SaveQueues(ctx.Queues);
            Audit.Log(Audit.Web, "删除调度队列", removed is null ? $"id={seg[1]}（不存在）" : $"{removed.Name}（id={seg[1]}）");
            await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
            return;
        }
        await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
    }

    /// <summary>队列顺序重排（v0.6.8+）：请求体携带完整 id 名单，与现有集合完全一致时按新顺序重赋 Index 落盘。</summary>
    private static async Task HandleReorderQueuesAsync(HttpListenerContext context, string body)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        JsonNode? node = HttpHelper.ParseBody(body);
        List<string>? ids = node?["ids"] is JsonArray array
            ? array.Select(item => item?.ToString() ?? "").ToList()
            : null;
        if (ids is null || ids.Count != ctx.Queues.Count
            || ids.Any(string.IsNullOrWhiteSpace)
            || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
        {
            await HttpHelper.WriteJsonAsync(context, new { error = "队列顺序名单缺失或与当前队列列表不一致" }, 400).ConfigureAwait(false);
            return;
        }
        HashSet<string> existing = new(ctx.Queues.Select(queue => queue.Id), StringComparer.Ordinal);
        if (ids.Any(id => !existing.Contains(id)))
        {
            await HttpHelper.WriteJsonAsync(context, new { error = "队列顺序名单与当前队列列表不一致" }, 400).ConfigureAwait(false);
            return;
        }
        Dictionary<string, DispatchQueue> byId = ctx.Queues.ToDictionary(queue => queue.Id, StringComparer.Ordinal);
        for (int i = 0; i < ids.Count; i++)
        {
            byId[ids[i]].Index = i;
        }
        DataStore.SaveQueues(ctx.Queues);
        Audit.Log(Audit.Web, "调整队列顺序", $"{ids.Count} 个调度队列");
        await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
    }

    /// <summary>定时时间严格 HH:mm 校验（v0.6.9+ P9）：「8:00」无前导零会被 Scheduler 解析失败静默跳过（定时不触发）。
    /// 保存即 400 报错，避免静默回退掩盖输入错误（NormalizeQueue 的回退保留给旧数据兼容）。</summary>
    private static string? CheckTimeFormat(DispatchQueue queue)
    {
        foreach (QueueTimeSet timeSet in queue.TimeSets)
        {
            if (!TimeOnly.TryParseExact(timeSet.Time, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            {
                return $"定时时间格式不正确（{timeSet.Time}），须为 HH:mm（如 08:00）";
            }
        }
        return null;
    }

    private static void NormalizeQueue(DispatchQueue queue)
    {
        if (!QueueRule.IsValidAutoRunMode(queue.AutoRunMode))
        {
            queue.AutoRunMode = "none";
        }
        if (!QueueRule.IsValidCompletionAction(queue.CompletionAction))
        {
            queue.CompletionAction = "none";
        }
        int index = 0;
        foreach (QueueTask task in queue.Tasks.OrderBy(task => task.Index))
        {
            task.Index = index++;
            if (string.IsNullOrWhiteSpace(task.Id))
            {
                task.Id = Guid.NewGuid().ToString("N");
            }
        }
        foreach (QueueTimeSet timeSet in queue.TimeSets)
        {
            if (string.IsNullOrWhiteSpace(timeSet.Id))
            {
                timeSet.Id = Guid.NewGuid().ToString("N");
            }
            if (!TimeOnly.TryParseExact(timeSet.Time, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            {
                timeSet.Time = "08:00";
            }
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var distinct = new List<QueueTimeSet>();
        foreach (QueueTimeSet timeSet in queue.TimeSets)
        {
            string key = $"{timeSet.Enabled}|{string.Join(",", timeSet.Days.OrderBy(day => day))}|{timeSet.Time}";
            if (!seen.Add(key))
            {
                continue;
            }
            distinct.Add(timeSet);
        }
        queue.TimeSets = distinct;
    }
}
