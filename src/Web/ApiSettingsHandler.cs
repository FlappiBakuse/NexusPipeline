using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
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
            if (node is null)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = "请求体无效" }, 400).ConfigureAwait(false);
                return;
            }
            AppSettings current = ctx.Settings;
            if (node is JsonObject json)
            {
                foreach (KeyValuePair<string, JsonNode?> pair in json)
                {
                    string field = pair.Key;
                    if (field is "secretKey" or "secretValue")
                    {
                        continue;
                    }
                    if (SecretFields.Contains(field) || ListFields.Contains(field))
                    {
                        continue;
                    }
                    JsonNode? value = pair.Value;
                    if (value is null)
                    {
                        continue;
                    }
                    string? error = BindField(current, field, value);
                    if (error is not null)
                    {
                        await HttpHelper.WriteJsonAsync(context, new { error }, 400).ConfigureAwait(false);
                        return;
                    }
                }
            }
            ConfigStore.Save(current);
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

    /// <summary>密钥字段（DPAPI 加密，仅经 secretKey/secretValue 协议写入；明文键不参与自动绑定）。</summary>
    private static readonly HashSet<string> SecretFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "webhookUrl", "webhookSecret", "smtpPassword", "accessToken",
    };

    /// <summary>集合字段（PUT 不绑定，保持现有语义）。</summary>
    private static readonly HashSet<string> ListFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "enabledPlugins", "disabledPlugins",
    };

    /// <summary>约定自动绑定：请求体字段名（camelCase）↔ AppSettings 属性（PascalCase）；带校验字段返回 400 错误文本，非法值按原语义静默忽略。</summary>
    private static string? BindField(AppSettings settings, string field, JsonNode value)
    {
        switch (field)
        {
            case "historyRetentionDays":
                int days = value.Int(settings.HistoryRetentionDays);
                string? check = Limits.CheckRetentionDays(days);
                if (check is not null)
                {
                    return check;
                }
                settings.HistoryRetentionDays = days;
                return null;
            case "webPort":
                int port = value.Int(settings.WebPort);
                if (port is >= 1024 and <= 65535)
                {
                    settings.WebPort = port;
                }
                return null;
            case "webhookTimeout":
                int timeout = value.Int(settings.WebhookTimeout);
                if (timeout >= 1)
                {
                    settings.WebhookTimeout = timeout;
                }
                return null;
            case "smtpPort":
                int smtpPort = value.Int(settings.SmtpPort);
                if (smtpPort is >= 1 and <= 65535)
                {
                    settings.SmtpPort = smtpPort;
                }
                return null;
            case "smtpTimeout":
                int smtpTimeout = value.Int(settings.SmtpTimeout);
                if (smtpTimeout >= 1)
                {
                    settings.SmtpTimeout = smtpTimeout;
                }
                return null;
            case "logLevel":
                string level = value.Str().Trim().ToLowerInvariant();
                if (LogLevelUtil.IsValid(level))
                {
                    settings.LogLevel = level;
                }
                return null;
        }
        string propertyName = char.ToUpperInvariant(field[0]) + field[1..];
        PropertyInfo? property = typeof(AppSettings).GetProperty(propertyName);
        if (property is null || !property.CanWrite)
        {
            return null;
        }
        if (property.PropertyType == typeof(bool))
        {
            bool currentValue = property.GetValue(settings) is bool b && b;
            property.SetValue(settings, value.Bool(currentValue));
        }
        else if (property.PropertyType == typeof(string))
        {
            property.SetValue(settings, value.Str());
        }
        return null;
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
