using System.Net;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.ControlPlane.Http;

/// <summary>
/// 更新 API：检查 / 状态 / 下载 / 应用 / 取消。
/// 远程访问时沿用 WebServer 统一 Bearer 令牌保护；本地请求豁免；
/// 应用门禁失败返回 409（前端提供「下次启动更新」defer 入口）。
/// </summary>
[ApiRoute("update")]
internal static class ApiUpdateHandler
{
    public static async Task Handle(
        HttpListenerContext context,
        string method,
        string[] seg,
        string body,
        UpdateService updates,
        UpdateAutomationService updateAutomation)
    {
        if (seg.Length != 2)
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        string sub = seg[1].ToLowerInvariant();
        switch (sub)
        {
            case "installer-apply" when method == "POST":
            {
                if (context.Request.RemoteEndPoint is not { } endpoint || !IPAddress.IsLoopback(endpoint.Address))
                { await HttpHelper.ErrorAsync(context, "operation_forbidden", 403).ConfigureAwait(false); return; }
                var node = HttpHelper.ParseBody(body);
                var result = updates.RequestInstallerApply(node?["stagedDir"]?.GetValue<string>() ?? "",
                    node?["version"]?.GetValue<string>() ?? "", node?["imageHash"]?.GetValue<string>() ?? "",
                    node?["transactionId"]?.GetValue<string>() ?? "", Audit.Web);
                if (!result.Succeeded)
                { await HttpHelper.ErrorAsync(context, result.Code ?? "installer_apply_failed", 409).ConfigureAwait(false); return; }
                await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false); return;
            }
            case "status" when method == "GET":
            {
                UpdateStatusSnapshot status = updates.GetStatus();
                await WriteStatusAsync(context, status, updateAutomation).ConfigureAwait(false);
                return;
            }
            case "check" when method == "POST":
            {
                    updates.ClearStartupAttempt();
                try
                {
                    UpdateStatusSnapshot status = await updates.CheckAsync(Audit.Web).ConfigureAwait(false);
                    await WriteStatusAsync(context, status, updateAutomation).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    string traceId = Guid.NewGuid().ToString("N");
                    Logger.Error($"[更新] 检查更新失败（追踪 {traceId}）：{ex}");
                    await HttpHelper.ErrorAsync(context, "update_check_failed", 500, new { traceId }).ConfigureAwait(false);
                }
                return;
            }
            case "download" when method == "POST":
            {
                    updates.ClearStartupAttempt();
                UpdateDownloadResult result = updates.StartDownload(Audit.Web);
                if (!result.Succeeded)
                {
                    await HttpHelper.ErrorAsync(context, result.Code ?? "update_download_rejected", 409).ConfigureAwait(false);
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
                    updates.ClearStartupAttempt();
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
                    await HttpHelper.ErrorAsync(context, result.Code ?? "update_apply_failed", statusCode).ConfigureAwait(false);
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

    private static async Task WriteStatusAsync(
        HttpListenerContext context,
        UpdateStatusSnapshot status,
        UpdateAutomationService updateAutomation)
    {
        UpdateAutomationSnapshot automation = updateAutomation.GetSnapshot();
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
            policyVerified = status.PolicyVerified,
            canDownload = status.CanDownload,
            manualUpdateRequired = status.ManualUpdateRequired,
            updateBlockCode = status.UpdateBlockCode,
            barrierVersion = status.BarrierVersion,
            migrationUrl = status.MigrationUrl,
            policyError = status.PolicyError,
            errorCode = string.IsNullOrWhiteSpace(status.Error) ? null : "update_failed",
            automation = new
            {
                checkEnabled = automation.CheckEnabled,
                autoUpdateEnabled = automation.AutoUpdateEnabled,
                lastCheckAt = automation.LastAutomaticCheckAt?.ToString("O"),
                nextCheckAt = automation.NextAutomaticCheckAt?.ToString("O"),
                waitingForIdle = automation.WaitingForIdle,
                idleBlockCode = automation.IdleBlockCode,
            },
        }).ConfigureAwait(false);
    }
}
