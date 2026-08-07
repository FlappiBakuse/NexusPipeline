using System.Net;

namespace NexusPipeline.Web;

internal static class ApiLogsHandler
{
    public static async Task Handle(HttpListenerContext context, string method)
    {
        if (method != "GET")
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        Audit.Log(Audit.Web, "查询运行日志尾部");
        await HttpHelper.WriteJsonAsync(context, HttpHelper.ReadLogTail(60)).ConfigureAwait(false);
    }
}
