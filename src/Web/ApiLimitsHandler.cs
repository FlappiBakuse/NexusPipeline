using System.Net;

namespace NexusPipeline.Web;

internal static class ApiLimitsHandler
{
    public static async Task Handle(HttpListenerContext context, string method)
    {
        if (method != "GET")
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        AppLimits l = Limits.Current;
        await HttpHelper.WriteJsonAsync(context, new
        {
            limits = new
            {
                l.MaxScripts,
                l.MaxUsersPerScript,
                l.MaxQueues,
                l.MaxTimeSetsPerQueue,
                l.MaxQueueTotalUsers,
                l.MaxScriptNameBytes,
                l.MaxQueueNameBytes,
                l.MinAttempts,
                l.MaxAttempts,
                l.MinStallMinutes,
                l.MaxStallMinutes,
                l.MinTotalMinutes,
                l.MaxTotalMinutes,
            },
            warnings = Limits.Warnings,
        }).ConfigureAwait(false);
    }
}
