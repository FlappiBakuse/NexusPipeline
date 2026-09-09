using System.Net;
using System.Text.Json.Nodes;
using NexusPipeline.Services;
using NexusPipeline.Utilities;

namespace NexusPipeline.Web;

[ApiRoute("appearance")]
internal static class ApiAppearanceHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg, string body)
    {
        AppearanceService service = RuntimeContext.Instance.Resolve<AppearanceService>();
        try
        {
            if (method == "GET" && seg.Length == 1)
            {
                await AppearanceApiSupport.WriteSnapshotAsync(context, service.GetSnapshot()).ConfigureAwait(false);
                return;
            }
            if (method == "PUT" && seg.Length == 1)
            {
                JsonNode? node = HttpHelper.ParseBody(body);
                if (node is not JsonObject patch)
                {
                    await HttpHelper.ErrorAsync(context, "invalid_config", 400).ConfigureAwait(false);
                    return;
                }
                string caller = AppearanceApiSupport.ResolveCaller(context, patch);
                await AppearanceApiSupport.WriteSnapshotAsync(context, service.Save(patch, caller)).ConfigureAwait(false);
                return;
            }
            if (method == "POST" && seg.Length == 2 && seg[1].Equals("rotation", StringComparison.OrdinalIgnoreCase))
            {
                await HttpHelper.ErrorAsync(context, "invalid_action", 400).ConfigureAwait(false);
                return;
            }
            if (method == "POST" && seg.Length == 3
                && seg[1].Equals("rotation", StringComparison.OrdinalIgnoreCase)
                && seg[2].Equals("startup", StringComparison.OrdinalIgnoreCase))
            {
                string caller = AppearanceApiSupport.ResolveCaller(context, null);
                await AppearanceApiSupport.WriteSnapshotAsync(context, service.StartStartupRotation(caller)).ConfigureAwait(false);
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
            Logger.Error($"[外观] 配置操作失败（追踪 {traceId}）：{ex}");
            await HttpHelper.ErrorAsync(context, "internal_error", 500, new { traceId }).ConfigureAwait(false);
        }
    }

}
