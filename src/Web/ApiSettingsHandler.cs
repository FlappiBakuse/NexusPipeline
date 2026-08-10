using System.Net;
using System.Text.Json.Nodes;

namespace NexusPipeline.Web;

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
            if (node is null)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = "请求体无效" }, 400).ConfigureAwait(false);
                return;
            }
            AppSettings current = ctx.Settings;
            if (node.Get("autoStart") is not null)
            {
                current.AutoStart = node.Get("autoStart").Bool(current.AutoStart);
            }
            if (node.Get("minimizeToTray") is not null)
            {
                current.MinimizeToTray = node.Get("minimizeToTray").Bool(current.MinimizeToTray);
            }
            if (node.Get("lightweightMode") is not null)
            {
                current.LightweightMode = node.Get("lightweightMode").Bool(current.LightweightMode);
            }
            if (node.Get("autoOpenBrowser") is not null)
            {
                current.AutoOpenBrowser = node.Get("autoOpenBrowser").Bool(current.AutoOpenBrowser);
            }
            if (node.Get("historyRetentionDays") is not null)
            {
                int days = node.Get("historyRetentionDays").Int(current.HistoryRetentionDays);
                string? check = Limits.CheckRetentionDays(days);
                if (check is not null)
                {
                    await HttpHelper.WriteJsonAsync(context, new { error = check }, 400).ConfigureAwait(false);
                    return;
                }
                current.HistoryRetentionDays = days;
            }
            if (node.Get("webPort") is not null)
            {
                int port = node.Get("webPort").Int(current.WebPort);
                if (port is >= 1024 and <= 65535)
                {
                    current.WebPort = port;
                }
            }
            if (node.Get("sendStrategy") is not null)
            {
                current.SendStrategy = node.Get("sendStrategy").Str();
            }
            if (node.Get("webhookEnabled") is not null)
            {
                current.WebhookEnabled = node.Get("webhookEnabled").Bool(current.WebhookEnabled);
            }
            if (node.Get("smtpEnabled") is not null)
            {
                current.SmtpEnabled = node.Get("smtpEnabled").Bool(current.SmtpEnabled);
            }
            if (node.Get("webhookType") is not null)
            {
                current.WebhookType = node.Get("webhookType").Str();
            }
            if (node.Get("webhookTemplate") is not null)
            {
                current.WebhookTemplate = node.Get("webhookTemplate").Str();
            }
            if (node.Get("webhookTimeout") is not null)
            {
                int timeout = node.Get("webhookTimeout").Int(current.WebhookTimeout);
                if (timeout >= 1)
                {
                    current.WebhookTimeout = timeout;
                }
            }
            if (node.Get("smtpHost") is not null)
            {
                current.SmtpHost = node.Get("smtpHost").Str();
            }
            if (node.Get("smtpPort") is not null)
            {
                int port = node.Get("smtpPort").Int(current.SmtpPort);
                if (port is >= 1 and <= 65535)
                {
                    current.SmtpPort = port;
                }
            }
            if (node.Get("smtpSecure") is not null)
            {
                current.SmtpSecure = node.Get("smtpSecure").Str();
            }
            if (node.Get("smtpUser") is not null)
            {
                current.SmtpUser = node.Get("smtpUser").Str();
            }
            if (node.Get("smtpFrom") is not null)
            {
                current.SmtpFrom = node.Get("smtpFrom").Str();
            }
            if (node.Get("smtpTo") is not null)
            {
                current.SmtpTo = node.Get("smtpTo").Str();
            }
            if (node.Get("smtpSubjectPrefix") is not null)
            {
                current.SmtpSubjectPrefix = node.Get("smtpSubjectPrefix").Str();
            }
            if (node.Get("smtpTimeout") is not null)
            {
                int timeout = node.Get("smtpTimeout").Int(current.SmtpTimeout);
                if (timeout >= 1)
                {
                    current.SmtpTimeout = timeout;
                }
            }
            if (node.Get("logLevel") is not null)
            {
                string level = node.Get("logLevel").Str().Trim().ToLowerInvariant();
                if (LogLevelUtil.IsValid(level))
                {
                    current.LogLevel = level;
                }
            }
            if (node.Get("allowRemoteAccess") is not null)
            {
                current.AllowRemoteAccess = node.Get("allowRemoteAccess").Bool(current.AllowRemoteAccess);
            }
            ConfigStore.Save(current);
            if (current.AllowRemoteAccess)
            {
                FirewallRule.EnsureAllowInbound();
            }
            TaskRegistration.SyncWithSettings(current);
            string secretDetail = "";
            if (node.Get("secretKey") is not null && node.Get("secretValue") is not null)
            {
                string key = node.Get("secretKey").Str();
                string value = node.Get("secretValue").Str();
                if (key is "webhookUrl" or "webhookSecret" or "smtpPassword" or "accessToken")
                {
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        ClearSecret(current, key);
                        secretDetail = $"，清除密钥 {key}";
                    }
                    else
                    {
                        SetSecret(current, key, value);
                        secretDetail = $"，更新密钥 {key}";
                    }
                    ConfigStore.Save(current);
                }
            }
            Audit.Log(Audit.Web, "保存设置", $"WebPort={current.WebPort}，AutoStart={(current.AutoStart ? "开" : "关")}，轻量={(current.LightweightMode ? "开" : "关")}{secretDetail}");
            await HttpHelper.WriteJsonAsync(context, new { ok = true, settings = MaskedSettings(current) }).ConfigureAwait(false);
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
            settings.SendStrategy,
            settings.WebhookEnabled,
            settings.SmtpEnabled,
            settings.WebhookType,
            webhookUrl = SecretStore.IsEncrypted(settings.WebhookUrl) ? "enc:***" : settings.WebhookUrl,
            webhookSecret = SecretStore.IsEncrypted(settings.WebhookSecret) ? "enc:***" : settings.WebhookSecret,
            settings.WebhookTemplate,
            settings.WebhookTimeout,
            settings.SmtpHost,
            settings.SmtpPort,
            settings.SmtpSecure,
            settings.SmtpUser,
            smtpPassword = SecretStore.IsEncrypted(settings.SmtpPassword) ? "enc:***" : settings.SmtpPassword,
            settings.SmtpFrom,
            settings.SmtpTo,
            settings.SmtpSubjectPrefix,
            settings.SmtpTimeout,
            settings.LogLevel,
            settings.AllowRemoteAccess,
            accessToken = settings.AccessToken.StartsWith("enc:", StringComparison.Ordinal) ? "enc:***" : settings.AccessToken,
        };
    }

    private static void SetSecret(AppSettings settings, string key, string value)
    {
        string encrypted = SecretStore.Encrypt(value);
        switch (key)
        {
            case "webhookUrl":
                settings.WebhookUrl = encrypted;
                break;
            case "webhookSecret":
                settings.WebhookSecret = encrypted;
                break;
            case "smtpPassword":
                settings.SmtpPassword = encrypted;
                break;
            case "accessToken":
                settings.AccessToken = encrypted;
                break;
        }
    }

    private static void ClearSecret(AppSettings settings, string key)
    {
        switch (key)
        {
            case "webhookUrl":
                settings.WebhookUrl = "";
                break;
            case "webhookSecret":
                settings.WebhookSecret = "";
                break;
            case "smtpPassword":
                settings.SmtpPassword = "";
                break;
            case "accessToken":
                settings.AccessToken = "";
                break;
        }
    }
}
