using System.Net;
using NexusPipeline.ControlPlane.Contracts;
using NexusPipeline.Host.Lifecycle;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Plugins.Runtime;
using NexusPipeline.Modules.Queues.Queries;
using NexusPipeline.Modules.Scheduling;
using NexusPipeline.Modules.Scripts.Queries;
using NexusPipeline.Modules.Settings.Contracts;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Modules.Updates;

namespace NexusPipeline.ControlPlane.Http;

[ApiRoute("status")]
internal static class ApiStatusHandler
{
    public static async Task Handle(
        HttpListenerContext context,
        string method,
        ISettingsProvider settings,
        Scheduler scheduler,
        ExecutionDispatcher dispatchCenter,
        ScriptQueries scriptQueries,
        QueueQueries queueQueries,
        PluginManager plugins)
    {
        if (method != "GET")
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(
            context,
            BuildStatus(context.Request.Locale, settings, scheduler, dispatchCenter, scriptQueries, queueQueries, plugins))
            .ConfigureAwait(false);
    }

    private static object BuildStatus(
        string locale,
        ISettingsProvider settingsProvider,
        Scheduler scheduler,
        ExecutionDispatcher dispatchCenter,
        ScriptQueries scriptQueries,
        QueueQueries queueQueries,
        PluginManager plugins)
    {
        AppSettings settings = settingsProvider.Current;
        var next = scheduler.NextTrigger();
        PendingSystemAction? pending = dispatchCenter.CurrentSystemAction;
        int scriptCount = scriptQueries.ListEffective().Count;
        IReadOnlyList<QueueReadModel> queues = queueQueries.List();
        int queueCount = queues.Count;
        int enabledQueues = queues.Count(queue => queue.Queue.NotifyEnabled);
        return new
        {
            service = ControlApiContract.ServiceName,
            controlApiVersion = ControlApiContract.Version,
            // 实例标识：每次进程启动重新生成，重启恢复据此区分重启前后的服务实例。
            instanceId = HostInstance.Id,
            // 本次进程由重启交接拉起时携带的交接标识；控制面前端用它确认应答来自本次重启的新实例。
            restartHandoffId = HostInstance.RestartHandoffId,
            time = DateTime.Now,
            lightweightMode = settings.LightweightMode,
            webPort = settings.WebPort,
            // 实际监听端口（端口冲突 +1 漂移/未重启时与配置端口不同），侧栏地址文案据此显示。
            actualPort = WebServer.Current?.Port ?? settings.WebPort,
            version = UpdateService.CurrentVersion,
            scriptCount,
            queueCount,
            nextSchedule = next is null ? null : new { queueName = next.Value.QueueName, time = next.Value.TriggerTime },
            systemAction = pending is null ? null : new { action = pending.Action, queueName = pending.QueueName, deadline = pending.Deadline },
            notifyStats = new
            {
                enabledQueues,
            },
            running = dispatchCenter.Active.Select(exec =>
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
                    logTruncated = snapshot.LogTruncated,
                    logTail = snapshot.LogTail,
                    logEntries = snapshot.LogEntries.Select(ToLogEntry).ToArray(),
                };
            }),
            plugins = plugins.GetLocalizedPluginManagementViews(locale),
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
