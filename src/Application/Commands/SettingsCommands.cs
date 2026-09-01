using System.Reflection;
using System.Text.Json.Nodes;
using NexusPipeline.App.Contracts;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using NexusPipeline.Utilities;

namespace NexusPipeline.App.Commands;

/// <summary>设置写入应用命令；HTTP 与 CLI 共用 clone-on-write、密钥处理和副作用收尾。</summary>
internal static class SettingsCommands
{
    private static readonly HashSet<string> SecretFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "webhookUrl", "webhookSecret", "smtpPassword", "proxyPassword", "accessToken",
        "feishuAppSecret", "slackBotToken", "dingTalkAppSecret",
    };

    public static OperationResult<AppSettings> Update(JsonObject patch, string source = Audit.Web)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        AppSettings candidate = ctx.Settings;
        string? bindError = null;
        string secretDetail = "";
        try
        {
            bool admitted = ctx.Center.TryExecuteHostConfigurationMutation(() =>
            {
                lock (ctx.SettingsMutationLock)
                {
                    candidate = ctx.Settings.Clone();
                    foreach (KeyValuePair<string, JsonNode?> pair in patch)
                    {
                        string field = pair.Key;
                        if (string.IsNullOrEmpty(field))
                        {
                            bindError = "请求体包含空字段名";
                            break;
                        }
                        if (field is "secretKey" or "secretValue" || SecretFields.Contains(field))
                        {
                            continue;
                        }
                        if (pair.Value is not null)
                        {
                            bindError = BindField(candidate, field, pair.Value);
                            if (bindError is not null)
                            {
                                break;
                            }
                        }
                    }

                    JsonNode? secretKeyNode = patch["secretKey"];
                    JsonNode? secretValueNode = patch["secretValue"];
                    if (bindError is null && secretKeyNode is not null && secretValueNode is not null)
                    {
                        string key = secretKeyNode.Str();
                        string value = secretValueNode.Str();
                        if (key is "webhookUrl" or "webhookSecret" or "smtpPassword" or "proxyPassword" or "accessToken"
                            or "feishuAppSecret" or "slackBotToken" or "dingTalkAppSecret")
                        {
                            if (string.IsNullOrWhiteSpace(value))
                            {
                                ClearSecret(candidate, key);
                                secretDetail = $"，清除密钥 {key}";
                            }
                            else
                            {
                                SetSecret(candidate, key, value);
                                secretDetail = $"，更新密钥 {key}";
                            }
                        }
                    }

                    if (bindError is null
                        && string.Equals(candidate.ProxyMode, "http", StringComparison.OrdinalIgnoreCase)
                        && string.IsNullOrWhiteSpace(candidate.ProxyUrl))
                    {
                        bindError = "自定义代理模式需要填写 HTTP/HTTPS 代理地址";
                    }

                    if (bindError is null)
                    {
                        ConfigStore.Save(candidate);
                        ctx.ReplaceSettings(candidate);
                    }
                }
            }, out string? failureCode);

            if (!admitted)
            {
                return OperationResult<AppSettings>.Failure(
                    failureCode ?? "host_maintenance",
                    "宿主正在进行维护操作，暂不能修改设置",
                    OperationErrorKind.Conflict);
            }

            if (bindError is not null)
            {
                return Validation<AppSettings>(bindError);
            }

            AppSettings current = ctx.Settings;
            if (current.AllowRemoteAccess && !current.LightweightMode)
            {
                FirewallRule.EnsureAllowInbound();
            }
            TaskRegistration.SyncWithSettings(current);
            Audit.Log(
                source,
                "保存设置",
                $"WebPort={current.WebPort}，AutoStart={(current.AutoStart ? "开" : "关")}，轻量={(current.LightweightMode ? "开" : "关")}{secretDetail}");
            return OperationResult<AppSettings>.Ok(current);
        }
        catch (Exception ex)
        {
            return Internal<AppSettings>(ex);
        }
    }

    private static string? BindField(AppSettings settings, string field, JsonNode value)
    {
        switch (field)
        {
            case "historyRetentionDays":
                int days = value.Int(settings.HistoryRetentionDays);
                string? retentionError = Limits.CheckRetentionDays(days);
                if (retentionError is not null)
                {
                    return retentionError;
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
            case "mcpPort":
                int mcpPort = value.Int(settings.McpPort);
                if (mcpPort is >= 1024 and <= 65535)
                {
                    settings.McpPort = mcpPort;
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
            case "proxyMode":
                string mode = value.Str().Trim().ToLowerInvariant();
                if (mode is not ("none" or "system" or "http"))
                {
                    return "代理模式无效（应为 none、system 或 http）";
                }
                settings.ProxyMode = mode;
                return null;
            case "proxyUrl":
                string proxyUrl = value.Str().Trim();
                if (!string.IsNullOrWhiteSpace(proxyUrl)
                    && (!Uri.TryCreate(proxyUrl, UriKind.Absolute, out Uri? proxyUri)
                        || (!string.Equals(proxyUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(proxyUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))))
                {
                    return "代理地址必须是 http:// 或 https:// 地址";
                }
                settings.ProxyUrl = proxyUrl;
                return null;
            case "proxyUsername":
                settings.ProxyUsername = value.Str().Trim();
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
            case "proxyPassword":
                settings.ProxyPassword = encrypted;
                break;
            case "accessToken":
                settings.AccessToken = encrypted;
                break;
            case "feishuAppSecret":
                settings.FeishuAppSecret = encrypted;
                break;
            case "slackBotToken":
                settings.SlackBotToken = encrypted;
                break;
            case "dingTalkAppSecret":
                settings.DingTalkAppSecret = encrypted;
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
            case "proxyPassword":
                settings.ProxyPassword = "";
                break;
            case "accessToken":
                settings.AccessToken = "";
                break;
            case "feishuAppSecret":
                settings.FeishuAppSecret = "";
                break;
            case "slackBotToken":
                settings.SlackBotToken = "";
                break;
            case "dingTalkAppSecret":
                settings.DingTalkAppSecret = "";
                break;
        }
    }

    private static OperationResult<T> Validation<T>(string message) =>
        OperationResult<T>.Failure("validation_error", message, OperationErrorKind.Validation);

    private static OperationResult<T> Internal<T>(Exception exception) =>
        OperationResult<T>.Failure("internal_error", exception.Message, OperationErrorKind.Internal);
}
