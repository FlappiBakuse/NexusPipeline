using System.Net;

namespace NexusPipeline.Web;

internal static class ApiQueuesHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg, string body)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        if (method == "GET" && seg.Length == 1)
        {
            Audit.Log(Audit.Web, "查询调度队列列表", $"{ctx.Queues.Count} 条");
            var result = ctx.Queues.Select(queue => new
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
                ?? Limits.CheckQueueTotalUsers(Limits.QueueTotalUsers(ctx, queue));
            if (limitError is not null)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = limitError }, 400).ConfigureAwait(false);
                return;
            }
            if (string.IsNullOrWhiteSpace(queue.Id) || ctx.FindQueue(queue.Id) is null)
            {
                queue.Id = Guid.NewGuid().ToString("N");
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
                ?? Limits.CheckQueueTotalUsers(Limits.QueueTotalUsers(ctx, update));
            if (limitError is not null)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = limitError }, 400).ConfigureAwait(false);
                return;
            }
            update.Id = existing.Id;
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
    }
}
