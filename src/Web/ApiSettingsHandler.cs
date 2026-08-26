using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using NexusPipeline.App.Commands;
using NexusPipeline.App.Contracts;
using NexusPipeline.Models;
using NexusPipeline.Services;
using NexusPipeline.Utilities;

namespace NexusPipeline.Web;

[ApiRoute("settings")]
internal static class ApiSettingsHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg, string body)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        if (method == "GET")
        {
            Audit.Log(Audit.Web, "查询设置");
            await HttpHelper.WriteJsonAsync(context, new
            {
                settings = MaskedSettings(ctx.Settings),
                status = new
                {
                    webhook = WebhookSender.Status(ctx.Settings),
                    smtp = SmtpSender.Status(ctx.Settings),
                    channels = new
                    {
                        webhookEnabled = ctx.Settings.WebhookEnabled,
                        smtpEnabled = ctx.Settings.SmtpEnabled,
                    },
                    autoStart = TaskRegistration.IsRegistered(),
                    remote = new
                    {
                        allowed = ctx.Settings.AllowRemoteAccess,
                        localOnly = !ctx.Settings.AllowRemoteAccess,
                        tokenSet = !string.IsNullOrWhiteSpace(ctx.Settings.AccessToken),
                        lanAddresses = ctx.Settings.AllowRemoteAccess ? NetInfo.ListLanAddresses() : new List<string>(),
                    },
                },
            }).ConfigureAwait(false);
            return;
        }
        if (method == "PUT" && seg.Length == 1)
        {
            JsonNode? node = HttpHelper.ParseBody(body);
            if (node is not JsonObject patch)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = "请求体无效" }, 400).ConfigureAwait(false);
                return;
            }
            OperationResult<AppSettings> result = SettingsCommands.Update(patch);
            if (!result.Succeeded)
            {
                await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
                return;
            }
            await HttpHelper.WriteJsonAsync(
                context,
                new { ok = true, settings = MaskedSettings(result.Value!) }).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && seg.Length == 2 && seg[1].ToLowerInvariant() == "test")
        {
            AppSettings settings = ctx.Settings;
            string text = $"[NexusPipeline] 通知测试\r\n时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n如果你收到这条消息，说明通知渠道配置正确。";
            bool ok = await NotifySender.SendAsync(settings, text).ConfigureAwait(false);
            Audit.Log(Audit.Web, "发送测试通知", ok ? "成功" : "失败");
            await HttpHelper.WriteJsonAsync(context, new { ok }).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && seg.Length == 2 && seg[1].ToLowerInvariant() == "restart")
        {
            if (ApplicationHost.IsWebOnly)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = "当前为仅网页模式（web），不支持自动重启，请手动重启" }, 400).ConfigureAwait(false);
                return;
            }
            if (ctx.Center.Active.Count > 0)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = "存在运行中的任务，请等待完成或取消后再重启" }, 409).ConfigureAwait(false);
                return;
            }
            if (UserConfigManager.EditSessions.Count > 0)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = "存在编辑配置会话，请结束后再重启" }, 409).ConfigureAwait(false);
                return;
            }
            if (ctx.Center.CurrentSystemAction is not null)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = "存在待执行的系统操作，请完成或取消后再重启" }, 409).ConfigureAwait(false);
                return;
            }
            int newPort = ctx.Settings.WebPort;
            Audit.Log(Audit.Web, "重启服务", $"端口 {newPort}");
            // 先响应 { ok, newPort }，后台拉起新进程（restart 分支会等待旧进程退出并接管），随后退出本进程。
            await HttpHelper.WriteJsonAsync(context, new { ok = true, newPort }).ConfigureAwait(false);
            _ = Task.Run(() =>
            {
                try
                {
                    Thread.Sleep(1000);
                    string exePath = Environment.ProcessPath ?? "";
                    if (string.IsNullOrWhiteSpace(exePath))
                    {
                        Logger.Error("[重启] 无法获取当前程序路径，放弃重启。");
                        return;
                    }
                    Process.Start(new ProcessStartInfo(exePath) { Arguments = "restart", UseShellExecute = false, CreateNoWindow = true });
                    Logger.Info("[重启] 已拉起新进程，即将退出当前进程。");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[重启] 拉起新进程失败：{ex.Message}");
                    return;
                }
                try
                {
                    if (!Bootstrap.TryRequestCompletionExit())
                    {
                        Logger.Warn("[重启] 当前进程仍有活动执行或编辑会话，已拒绝退出；新进程可能需要手动处理。");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[重启] 退出当前进程失败：{ex.Message}");
                    try
                    {
                        Environment.Exit(0);
                    }
                    catch
                    {
                    }
                }
            });
            return;
        }
        await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
    }

    private static object MaskedSettings(AppSettings settings)
    {
        return new
        {
            settings.AutoStart,
            settings.MinimizeToTray,
            settings.LightweightMode,
            settings.AutoOpenBrowser,
            settings.HistoryRetentionDays,
            settings.WebPort,
            settings.WebhookEnabled,
            settings.SmtpEnabled,
            settings.WebhookType,
            // 密钥一律不回显明文——空=未设置；非空（无论是否 DPAPI 加密，含旧版明文遗留
            // 与手工编辑的明文）统一返回占位符，杜绝明文泄露；前端协议为「非空=已设置，留空不变」。
            webhookUrl = string.IsNullOrWhiteSpace(settings.WebhookUrl) ? "" : "enc:***",
            webhookSecret = string.IsNullOrWhiteSpace(settings.WebhookSecret) ? "" : "enc:***",
            settings.WebhookTemplate,
            settings.WebhookTimeout,
            settings.SmtpHost,
            settings.SmtpPort,
            settings.SmtpSecure,
            settings.SmtpUser,
            smtpPassword = string.IsNullOrWhiteSpace(settings.SmtpPassword) ? "" : "enc:***",
            settings.SmtpFrom,
            settings.SmtpTo,
            settings.SmtpSubjectPrefix,
            settings.SmtpTimeout,
            settings.LogLevel,
            settings.AllowRemoteAccess,
            accessToken = string.IsNullOrWhiteSpace(settings.AccessToken) ? "" : "enc:***",
            settings.UpdateCheckEnabled,
            settings.UpdateChannel,
            settings.UpdateSourceUrl,
        };
    }

}
