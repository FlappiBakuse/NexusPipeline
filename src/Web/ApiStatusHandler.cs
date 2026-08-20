using System.Net;
using NexusPipeline.Models;
using NexusPipeline.Extensibility;
using NexusPipeline.Services;

namespace NexusPipeline.Web;

[ApiRoute("status")]
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
        PendingSystemAction? pending = RuntimeContext.Instance.Center.CurrentSystemAction;
        // v0.7.2+（KN-04）：锁内读取计数，避免与并发修改冲突（「集合已修改」）。
        int scriptCount, queueCount, enabledScripts, enabledQueues;
        lock (RuntimeContext.Instance.DataLock)
        {
            scriptCount = RuntimeContext.Instance.Scripts.Count;
            queueCount = RuntimeContext.Instance.Queues.Count;
            enabledScripts = RuntimeContext.Instance.Scripts.Count(script => script.NotifyEnabled);
            enabledQueues = RuntimeContext.Instance.Queues.Count(queue => queue.NotifyEnabled);
        }
        return new
        {
            time = DateTime.Now,
            lightweightMode = settings.LightweightMode,
            webPort = settings.WebPort,
            // v0.7.4（KN-47）：实际监听端口（端口冲突 +1 漂移/未重启时与配置端口不同），侧栏地址文案据此显示。
            actualPort = WebServer.Current?.Port ?? settings.WebPort,
            version = typeof(WebServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            scriptCount,
            queueCount,
            nextSchedule = next is null ? null : new { queueName = next.Value.QueueName, time = next.Value.TriggerTime },
            systemAction = pending is null ? null : new { action = pending.Action, queueName = pending.QueueName, deadline = pending.Deadline },
            notifyStats = new
            {
                enabledScripts,
                enabledQueues,
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
            plugins = RuntimeContext.Instance.Plugins.PluginSummaries.Select(plugin => new
            {
                plugin.Name,
                plugin.DisplayName,
                gameName = plugin.GameName,
                plugin.Description,
                plugin.Version,
                plugin.IsBuiltIn,
                kind = plugin.Kind,
                supportsEmulator = RuntimeContext.Instance.Plugins.HasCapability(plugin.Name, PluginCapabilityKeys.Emulator),
                enabled = RuntimeContext.Instance.Plugins.IsEnabled(plugin.Name),
            }),
        };
    }
}
