using System.Net;
using NexusPipeline.Services;

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
                await HttpHelper.WriteJsonAsync(context, new { ok = false, code = "too_large", error = "壁纸文件不能超过 8192 KB" }, 413).ConfigureAwait(false);
                return;
            }
            string caller = ApiAppearanceHandler.ResolveCaller(context, null);
            AppearanceAsset asset = await RuntimeContext.Instance.Resolve<AppearanceService>().UploadAsync(
                context.Request.InputStream,
                context.Request.ContentType,
                context.Request.Headers["X-Nexus-Original-Name"] ?? context.Request.QueryString["name"],
                context.Request.ContentLength64,
                caller).ConfigureAwait(false);
            await HttpHelper.WriteJsonAsync(context, new { ok = true, asset = ApiAppearanceHandler.ToAssetDto(asset) }).ConfigureAwait(false);
        }
        catch (AppearanceException ex)
        {
            await HttpHelper.WriteJsonAsync(context, new { ok = false, code = ex.Code, error = ex.Message }, ApiAppearanceHandler.StatusCode(ex.Code)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await HttpHelper.WriteJsonAsync(context, new { ok = false, code = "internal_error", error = ex.Message }, 500).ConfigureAwait(false);
        }
    }
}
