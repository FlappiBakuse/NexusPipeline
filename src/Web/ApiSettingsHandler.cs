using System.Net;
using System.Text.Json.Nodes;
using NexusPipeline.App.Commands;
using NexusPipeline.App.Contracts;
using NexusPipeline.Localization;
using NexusPipeline.Models;
using NexusPipeline.Services;
using NexusPipeline.Services.Networking;
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
                        webhookScreenshotEnabled = ctx.Settings.WebhookScreenshotEnabled,
                        smtpEnabled = ctx.Settings.SmtpEnabled,
                        smtpScreenshotEnabled = ctx.Settings.SmtpScreenshotEnabled,
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
                await HttpHelper.ErrorAsync(context, "invalid_request", 400).ConfigureAwait(false);
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
            string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", LocaleCatalog.Culture(LocaleCatalog.HostLocale));
            string text = string.Join(
                "\r\n",
                HostLocalization.TranslateNamed(
                    "notification.test.title",
                    "[NexusPipeline] 通知测试",
                    locale: LocaleCatalog.HostLocale),
                HostLocalization.TranslateNamed(
                    "notification.test.body",
                    $"时间：{time}\r\n如果你收到这条消息，说明通知渠道配置正确。",
                    new Dictionary<string, object?> { ["time"] = time },
                    LocaleCatalog.HostLocale));
            bool ok = await NotifySender.SendAsync(
                settings,
                text,
                outbound: ctx.Resolve<OutboundHttpClientProvider>()).ConfigureAwait(false);
            Audit.Log(Audit.Web, "发送测试通知", ok ? "成功" : "失败");
            if (!ok)
            {
                const string code = "notification_test_failed";
                await HttpHelper.ErrorAsync(context, code, 502).ConfigureAwait(false);
                return;
            }
            await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && seg.Length == 2 && seg[1].ToLowerInvariant() == "restart")
        {
            int newPort = ctx.Settings.WebPort;
            RestartRequestResult restart = Bootstrap.RequestRestart(Audit.Web);
            if (!restart.Accepted)
            {
                int status = restart.Code == "operation_forbidden" ? 400 : 409;
                await HttpHelper.ErrorAsync(context, restart.Code, status).ConfigureAwait(false);
                return;
            }
            // 接受重启前已经取得维护租约，后台生命周期不会再重新做一次易竞态的状态检查。
            await HttpHelper.WriteJsonAsync(context, new { ok = true, newPort }).ConfigureAwait(false);
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
            settings.McpEnabled,
            settings.McpPort,
            settings.ProxyMode,
            settings.ProxyUrl,
            settings.ProxyUsername,
            proxyPassword = string.IsNullOrWhiteSpace(settings.ProxyPassword) ? "" : "enc:***",
            settings.WebhookEnabled,
            settings.WebhookScreenshotEnabled,
            settings.SmtpEnabled,
            settings.SmtpScreenshotEnabled,
            settings.WebhookType,
            // 密钥一律不回显明文——空=未设置；非空（无论是否 DPAPI 加密，含旧版明文遗留
            // 与手工编辑的明文）统一返回占位符，杜绝明文泄露；前端协议为「非空=已设置，留空不变」。
            webhookUrl = string.IsNullOrWhiteSpace(settings.WebhookUrl) ? "" : "enc:***",
            webhookSecret = string.IsNullOrWhiteSpace(settings.WebhookSecret) ? "" : "enc:***",
            settings.WebhookTemplate,
            settings.WebhookTimeout,
            settings.FeishuAppId,
            feishuAppSecret = string.IsNullOrWhiteSpace(settings.FeishuAppSecret) ? "" : "enc:***",
            settings.SlackChannelId,
            slackBotToken = string.IsNullOrWhiteSpace(settings.SlackBotToken) ? "" : "enc:***",
            settings.DingTalkAppKey,
            dingTalkAppSecret = string.IsNullOrWhiteSpace(settings.DingTalkAppSecret) ? "" : "enc:***",
            settings.DingTalkRobotCode,
            settings.DingTalkOpenConversationId,
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
            settings.HostLocale,
            settings.AllowRemoteAccess,
            accessToken = string.IsNullOrWhiteSpace(settings.AccessToken) ? "" : "enc:***",
            settings.UpdateCheckEnabled,
            settings.UpdateAutoApplyEnabled,
            settings.UpdateChannel,
            settings.UpdateSourceUrl,
        };
    }

}
