using System.Net;
using System.Text.Json.Nodes;
using NexusPipeline.Services;
using NexusPipeline.Utilities;

namespace NexusPipeline.Web;

[ApiRoute("appearance-assets")]
internal static class ApiAppearanceAssetsHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg, string body)
    {
        AppearanceService service = RuntimeContext.Instance.Resolve<AppearanceService>();
        try
        {
            if (method is "GET" or "HEAD" && seg.Length == 2)
            {
                string id = Uri.UnescapeDataString(seg[1]);
                if (!service.TryGetAssetPath(id, out string? path, out _)
                    || path is null)
                {
                    await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
                    return;
                }
                HttpHelper.ServeFile(context, path);
                return;
            }
            if (method == "DELETE" && seg.Length == 2)
            {
                string caller = AppearanceApiSupport.ResolveCaller(context, null);
                await AppearanceApiSupport.WriteSnapshotAsync(
                    context,
                    service.Delete(caller, Uri.UnescapeDataString(seg[1]))).ConfigureAwait(false);
                return;
            }
            if (method == "PUT" && seg.Length == 3 && seg[2].Equals("palette", StringComparison.OrdinalIgnoreCase))
            {
                JsonNode? node = HttpHelper.ParseBody(body);
                if (node is not JsonObject palette)
                {
                    await HttpHelper.ErrorAsync(context, "invalid_palette", 400).ConfigureAwait(false);
                    return;
                }
                string caller = AppearanceApiSupport.ResolveCaller(context, palette);
                await AppearanceApiSupport.WriteSnapshotAsync(
                    context,
                    service.SavePalette(caller, Uri.UnescapeDataString(seg[1]), palette)).ConfigureAwait(false);
                return;
            }
            if (method == "GET" && seg.Length == 1)
            {
                await AppearanceApiSupport.WriteSnapshotAsync(context, service.GetSnapshot()).ConfigureAwait(false);
                return;
            }
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
        }
        catch (AppearanceException ex)
        {
            await HttpHelper.ErrorAsync(context, ex.Code, AppearanceApiSupport.StatusCode(ex.Code)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            string traceId = Guid.NewGuid().ToString("N");
            Logger.Error($"[外观] 资源操作失败（追踪 {traceId}）：{ex}");
            await HttpHelper.ErrorAsync(context, "internal_error", 500, new { traceId }).ConfigureAwait(false);
        }
    }
}
