using System.Net;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Diagnostics;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Serialization;

namespace NexusPipeline.ControlPlane.Http;

[ApiRoute("diagnostics")]
internal static class ApiDiagnosticsHandler
{
    public static async Task Handle(
        HttpListenerContext context,
        string method,
        string[] seg,
        string body,
        DiagnosticsService diagnostics)
    {
        if (seg.Length >= 2 && seg[1].Equals("export", StringComparison.OrdinalIgnoreCase))
        {
            await HandleExportAsync(context, method, body, diagnostics).ConfigureAwait(false);
            return;
        }
        if (method != "GET")
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        DiagnosticSnapshot snapshot = diagnostics.CreateSnapshot();
        await HttpHelper.WriteJsonAsync(context, snapshot).ConfigureAwait(false);
    }

    private static async Task HandleExportAsync(
        HttpListenerContext context,
        string method,
        string body,
        DiagnosticsService diagnostics)
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
            DiagnosticBundleResult result = diagnostics.ExportSupportBundle(outputPath);
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
