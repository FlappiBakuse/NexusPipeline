using System.Net;
using NexusPipeline.App.Contracts;
using NexusPipeline.App.Queries;
using NexusPipeline.Models;
using NexusPipeline.Plugins;
using NexusPipeline.Extensibility;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;

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
        RuntimeContext ctx = RuntimeContext.Instance;
        AppSettings settings = ctx.Settings;
        var next = ctx.Scheduler.NextTrigger();
        PendingSystemAction? pending = ctx.Center.CurrentSystemAction;
        int scriptCount = ctx.Resolve<ScriptQueries>().ListEffective().Count;
        IReadOnlyList<QueueReadModel> queues = ctx.Resolve<QueueQueries>().List();
        int queueCount = queues.Count;
        int enabledQueues = queues.Count(queue => queue.Queue.NotifyEnabled);
        return new
        {
            service = ControlApiContract.ServiceName,
            controlApiVersion = ControlApiContract.Version,
            time = DateTime.Now,
            lightweightMode = settings.LightweightMode,
            webPort = settings.WebPort,
            // 实际监听端口（端口冲突 +1 漂移/未重启时与配置端口不同），侧栏地址文案据此显示。
            actualPort = WebServer.Current?.Port ?? settings.WebPort,
            version = typeof(WebServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            scriptCount,
            queueCount,
            nextSchedule = next is null ? null : new { queueName = next.Value.QueueName, time = next.Value.TriggerTime },
            systemAction = pending is null ? null : new { action = pending.Action, queueName = pending.QueueName, deadline = pending.Deadline },
            notifyStats = new
            {
                enabledQueues,
            },
            running = ctx.Center.Active.Select(exec =>
            {
                RunningExecutionSnapshot snapshot = exec.Snapshot();
                return new
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
                    logEntries = snapshot.LogEntries.Select(ToLogEntry).ToArray(),
                };
            }),
            plugins = ctx.Plugins.PluginManagementViews,
        };
    }

    private static object ToLogEntry(ExecutionLogEntry entry) => new
    {
        sequence = entry.Sequence,
        timestamp = entry.Timestamp,
        level = entry.Level.ToString().ToLowerInvariant(),
        text = entry.FormattedText,
    };
}
