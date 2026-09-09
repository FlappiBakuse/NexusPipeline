using System.Net;
using System.Text.Json.Nodes;
using NexusPipeline.Persistence;
using NexusPipeline.Services.Diagnostics;
using NexusPipeline.Utilities;

namespace NexusPipeline.Web;

[ApiRoute("diagnostics")]
internal static class ApiDiagnosticsHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg, string body)
    {
        if (seg.Length >= 2 && seg[1].Equals("export", StringComparison.OrdinalIgnoreCase))
        {
            await HandleExportAsync(context, method, body).ConfigureAwait(false);
            return;
        }
        if (method != "GET")
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        DiagnosticSnapshot snapshot = RuntimeContext.Instance.Resolve<DiagnosticsService>().CreateSnapshot();
        await HttpHelper.WriteJsonAsync(context, snapshot).ConfigureAwait(false);
    }

    private static async Task HandleExportAsync(HttpListenerContext context, string method, string body)
    {
        if (method != "POST")
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        if (!HttpHelper.IsLoopback(context))
        {
            await HttpHelper.ErrorAsync(context, "local_only", 403).ConfigureAwait(false);
            return;
        }

        JsonNode? node = HttpHelper.ParseBody(body);
        string? outputPath = node?.Get("outputPath").Str();
        try
        {
            DiagnosticBundleResult result = RuntimeContext.Instance.Resolve<DiagnosticsService>()
                .ExportSupportBundle(outputPath);
            await HttpHelper.WriteJsonAsync(context, new
            {
                ok = true,
                path = result.Path,
                sizeBytes = result.SizeBytes,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            string traceId = Guid.NewGuid().ToString("N");
            Logger.Error($"[诊断] 导出支持包失败（追踪 {traceId}）：{ex}");
            await HttpHelper.ErrorAsync(context, "diagnostics_export_failed", 500, new { traceId }).ConfigureAwait(false);
        }
    }
}
