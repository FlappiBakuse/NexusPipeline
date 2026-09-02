using System.Net;
using NexusPipeline.Services;
using NexusPipeline.Services.Update;

namespace NexusPipeline.Web;

/// <summary>
/// 更新 API：检查 / 状态 / 下载 / 应用 / 取消。
/// 远程访问时沿用 WebServer 统一 Bearer 令牌保护；本地请求豁免；
/// 应用门禁失败返回 409（前端提供「下次启动更新」defer 入口）。
/// </summary>
[ApiRoute("update")]
internal static class ApiUpdateHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg, string body)
    {
        if (seg.Length != 2)
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        RuntimeContext ctx = RuntimeContext.Instance;
        UpdateService updates = ctx.Resolve<UpdateService>();
        string sub = seg[1].ToLowerInvariant();
        switch (sub)
        {
            case "status" when method == "GET":
            {
                UpdateStatusSnapshot status = updates.GetStatus();
                await WriteStatusAsync(context, status).ConfigureAwait(false);
                return;
            }
            case "check" when method == "POST":
            {
                try
                {
                    UpdateStatusSnapshot status = await updates.CheckAsync(Audit.Web).ConfigureAwait(false);
                    await WriteStatusAsync(context, status).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await HttpHelper.WriteJsonAsync(context, new { error = $"检查更新失败：{ex.Message}" }, 500).ConfigureAwait(false);
                }
                return;
            }
            case "download" when method == "POST":
            {
                string? error = updates.StartDownload(Audit.Web);
                if (error is not null)
                {
                    await HttpHelper.WriteJsonAsync(context, new { ok = false, error }, 409).ConfigureAwait(false);
                    return;
                }
                await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
                return;
            }
            case "cancel" when method == "POST":
            {
                bool canceled = updates.CancelDownload();
                await HttpHelper.WriteJsonAsync(context, new { ok = canceled }).ConfigureAwait(false);
                return;
            }
            case "apply" when method == "POST":
            {
                bool defer = false;
                System.Text.Json.Nodes.JsonNode? node = HttpHelper.ParseBody(body);
                if (node is not null)
                {
                    defer = node["defer"]?.GetValue<bool>() == true;
                }
                UpdateApplyResult result = updates.RequestApply(defer, Audit.Web);
                if (!result.Succeeded)
                {
                    int statusCode = result.Code is "busy" or "not-ready" ? 409 : 400;
                    await HttpHelper.WriteJsonAsync(context, new { ok = false, error = result.Error, code = result.Code }, statusCode).ConfigureAwait(false);
                    return;
                }
                await HttpHelper.WriteJsonAsync(context, new { ok = true, deferred = result.Deferred }).ConfigureAwait(false);
                return;
            }
            default:
                await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
                return;
        }
    }

    private static async Task WriteStatusAsync(HttpListenerContext context, UpdateStatusSnapshot status)
    {
        await HttpHelper.WriteJsonAsync(context, new
        {
            state = status.State.ToString().ToLowerInvariant(),
            current = status.Current,
            latest = status.Latest,
            channel = status.Channel,
            available = status.Available,
            @checked = status.HasChecked,
            prerelease = status.LatestPrerelease == true,
            notes = status.Notes,
            progress = status.Progress,
            bytesRead = status.BytesRead,
            bytesTotal = status.BytesTotal,
            error = status.Error,
        }).ConfigureAwait(false);
    }
}
