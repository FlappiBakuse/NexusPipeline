using System.Net;
using NexusPipeline.Services;
using NexusPipeline.Utilities;

namespace NexusPipeline.Web;

[ApiRoute("appearance-upload", BodyMode = ApiBodyMode.Raw, MaxBodyBytes = 8 * 1024 * 1024)]
internal static class ApiAppearanceUploadHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg, string body)
    {
        if (method != "POST" || seg.Length != 1)
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        try
        {
            if (context.Request.ContentLength64 > AppearanceService.MaxAssetBytes)
            {
                await HttpHelper.ErrorAsync(context, "too_large", 413, new { maxKb = AppearanceService.MaxAssetBytes / 1024 }).ConfigureAwait(false);
                return;
            }
            string caller = AppearanceApiSupport.ResolveCaller(context, null);
            AppearanceAsset asset = await RuntimeContext.Instance.Resolve<AppearanceService>().UploadAsync(
                context.Request.InputStream,
                context.Request.ContentType,
                context.Request.Headers["X-Nexus-Original-Name"] ?? context.Request.QueryString["name"],
                context.Request.ContentLength64,
                caller).ConfigureAwait(false);
            await HttpHelper.WriteJsonAsync(context, new { ok = true, asset = AppearanceApiSupport.ToAssetDto(asset) }).ConfigureAwait(false);
        }
        catch (AppearanceException ex)
        {
            await HttpHelper.ErrorAsync(context, ex.Code, AppearanceApiSupport.StatusCode(ex.Code)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            string traceId = Guid.NewGuid().ToString("N");
            Logger.Error($"[外观] 文件上传失败（追踪 {traceId}）：{ex}");
            await HttpHelper.ErrorAsync(context, "internal_error", 500, new { traceId }).ConfigureAwait(false);
        }
    }
}
