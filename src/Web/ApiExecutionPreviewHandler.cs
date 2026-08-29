using System.Net;
using NexusPipeline.Services.Execution;

namespace NexusPipeline.Web;

[ApiRoute("execution-preview")]
internal static class ApiExecutionPreviewHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg, string body)
    {
        if (method != "GET" || seg.Length != 2)
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }

        string runId = Uri.UnescapeDataString(seg[1]);
        string pluginName = context.Request.QueryString["plugin"]?.Trim() ?? "";
        ExecutionPreviewResponse response = await RuntimeContext.Instance
            .Resolve<ExecutionPreviewService>()
            .CaptureAsync(runId, pluginName)
            .ConfigureAwait(false);
        if (response.StatusCode == 204)
        {
            var headers = new Dictionary<string, string>
            {
                ["Cache-Control"] = "no-store",
                ["X-Nexus-Preview-State"] = response.State ?? "waiting_for_game",
            };
            if (!string.IsNullOrWhiteSpace(response.Source))
            {
                headers["X-Nexus-Preview-Source"] = response.Source;
            }
            await HttpHelper.NoContentAsync(context, headers).ConfigureAwait(false);
            return;
        }
        if (response.StatusCode != 200 || response.Data is null)
        {
            await HttpHelper.WriteJsonAsync(
                context,
                new { error = response.Error ?? "实时截图暂不可用" },
                response.StatusCode).ConfigureAwait(false);
            return;
        }

        var imageHeaders = new Dictionary<string, string>
        {
            ["X-Nexus-Preview-Source"] = response.Source ?? "",
            ["X-Nexus-Preview-Captured-At"] = response.CapturedAt?.ToString("O") ?? "",
        };
        await HttpHelper.WriteBinaryAsync(context, response.Data, response.ContentType, imageHeaders).ConfigureAwait(false);
    }
}
