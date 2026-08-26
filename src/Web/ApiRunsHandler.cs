using System.Net;
using NexusPipeline.Services;

namespace NexusPipeline.Web;

/// <summary>运行任务列表 API；详情与取消继续由 dispatch/cancel 兼容路由提供。</summary>
[ApiRoute("runs")]
internal static class ApiRunsHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg, string body)
    {
        if (method != "GET" || seg.Length != 1)
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }

        var running = RuntimeContext.Instance.Center.Active
            .Select(exec =>
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
                    snapshot.CurrentStatus,
                    snapshot.CurrentAttempt,
                    snapshot.CurrentMaxAttempts,
                    persistenceWarning = snapshot.PersistenceWarning,
                    logTail = snapshot.LogTail,
                };
            })
            .ToList();
        await HttpHelper.WriteJsonAsync(context, running).ConfigureAwait(false);
    }
}
