using System.Net;
using NexusPipeline.Plugins;

namespace NexusPipeline.Web;

internal static class ApiStatusHandler
{
    public static async Task Handle(HttpListenerContext context, string method)
    {
        if (method != "GET")
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, BuildStatus()).ConfigureAwait(false);
    }

    private static object BuildStatus()
    {
        AppSettings settings = RuntimeContext.Instance.Settings;
        var next = RuntimeContext.Instance.Scheduler.NextTrigger();
        return new
        {
            time = DateTime.Now,
            lightweightMode = settings.LightweightMode,
            webPort = settings.WebPort,
            version = typeof(WebServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            scriptCount = RuntimeContext.Instance.Scripts.Count,
            queueCount = RuntimeContext.Instance.Queues.Count,
            nextSchedule = next is null ? null : new { queueName = next.Value.QueueName, time = next.Value.TriggerTime },
            notifyStats = new
            {
                enabledScripts = RuntimeContext.Instance.Scripts.Count(script => script.NotifyEnabled),
                enabledQueues = RuntimeContext.Instance.Queues.Count(queue => queue.NotifyEnabled),
            },
            running = RuntimeContext.Instance.Center.Active.Select(exec => new
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
            }),
            plugins = RuntimeContext.Instance.Plugins.Plugins.Select(plugin => new
            {
                plugin.Name,
                plugin.DisplayName,
                plugin.Description,
                plugin.Version,
                plugin.IsBuiltIn,
                kind = plugin is ISpecializedScriptPlugin ? "specialized" : "general",
                enabled = RuntimeContext.Instance.Plugins.IsEnabled(plugin.Name),
            }),
        };
    }
}
