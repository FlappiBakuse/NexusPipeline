using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

internal static class WebhookSender
{
    private static readonly HttpClient Http = new();

    private static readonly string[] Types = { "feishu", "dingtalk", "wecom", "slack", "discord", "generic" };

    public static string TypeDisplay(string type)
    {
        return type switch
        {
            "feishu" => "飞书",
            "dingtalk" => "钉钉",
            "wecom" => "企业微信",
            "slack" => "Slack",
            "discord" => "Discord",
            "generic" => "自定义模板",
            _ => type,
        };
    }

    public static (bool Ok, string Reason) Status(AppSettings settings)
    {
        string url = settings.WebhookUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return (false, "未配置（未设置 Webhook 地址）");
        }
        string type = settings.WebhookType;
        if (!Types.Contains(type))
        {
            return (false, $"未配置（未知的 webhook_type：{type}）");
        }
        if (type == "generic" && string.IsNullOrWhiteSpace(settings.WebhookTemplate))
        {
            return (false, "未配置（generic 类型缺少 webhook_template）");
        }
        if (SecretStore.IsEncrypted(url) && !SecretStore.TryDecrypt(url, out _))
        {
            return (false, "未配置（密钥无法解密，可能已复制到其他电脑）");
        }
        return (true, $"已配置（{TypeDisplay(type)}）");
    }

    public static async Task<bool> SendAsync(AppSettings settings, string text)
    {
        string? webhookUrl = SecretStore.TryDecrypt(settings.WebhookUrl, out string? url) ? url : null;
        string? webhookSecret = SecretStore.TryDecrypt(settings.WebhookSecret, out string? secret) ? secret : null;
        string type = settings.WebhookType;
        string template = settings.WebhookTemplate;
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            Logger.Error("[错误] 未配置 Webhook 地址，无法发送。");
            return false;
        }
        if (type == "generic" && string.IsNullOrWhiteSpace(template))
        {
            Logger.Error("[错误] webhook_type=generic 但未配置 webhook_template，无法发送。");
            return false;
        }
        int timeout = settings.WebhookTimeout < 1 ? 30 : settings.WebhookTimeout;
        string body = BuildBody(type, text, webhookSecret, template);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            using var response = await Http.SendAsync(request, cts.Token).ConfigureAwait(false);
            string responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            bool ok = type is "feishu" or "dingtalk"
                ? (JsonNode.Parse(responseText).Get("code").Int(-1) == 0)
                : response.IsSuccessStatusCode;
            if (ok)
            {
                Logger.Info($"{TypeDisplay(type)} Webhook 发送成功。");
            }
            else
            {
                Logger.Warn($"[警告] Webhook 返回异常：HTTP {(int)response.StatusCode} {responseText}");
            }
            return ok;
        }
        catch (Exception ex)
        {
            Logger.Error($"[错误] Webhook 发送失败：{ex.Message}");
            return false;
        }
    }

    private static string BuildBody(string type, string text, string? secret, string template)
    {
        string literal = JsonLiteral(text);
        switch (type)
        {
            case "dingtalk":
            {
                string body = $"{{\"msgtype\":\"text\",\"text\":{{\"content\":{literal}}}";
                if (!string.IsNullOrWhiteSpace(secret))
                {
                    string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                    body = $"{{\"timestamp\":{JsonLiteral(timestamp)},\"sign\":{JsonLiteral(Sign(timestamp, secret, hex: true))},\"msgtype\":\"text\",\"text\":{{\"content\":{literal}}}";
                }
                return body + "}";
            }
            case "wecom":
                return $"{{\"msgtype\":\"text\",\"text\":{{\"content\":{literal}}}}}";
            case "slack":
                return $"{{\"text\":{literal}}}";
            case "discord":
                return $"{{\"content\":{literal}}}";
            case "generic":
                return string.IsNullOrWhiteSpace(template) ? literal : template.Replace("{text}", literal, StringComparison.Ordinal);
            case "feishu":
            default:
            {
                string body = $"{{\"msg_type\":\"text\",\"content\":{{\"text\":{literal}}}";
                if (!string.IsNullOrWhiteSpace(secret))
                {
                    string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                    body = $"{{\"timestamp\":{JsonLiteral(timestamp)},\"sign\":{JsonLiteral(Sign(timestamp, secret, hex: false))},\"msg_type\":\"text\",\"content\":{{\"text\":{literal}}}";
                }
                return body + "}";
            }
        }
    }

    private static string Sign(string timestamp, string secret, bool hex)
    {
        byte[] key = Encoding.UTF8.GetBytes($"{timestamp}\n{secret}");
        using var hmac = new HMACSHA256(key);
        byte[] digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(""));
        return hex ? Convert.ToHexString(digest).ToLowerInvariant() : Convert.ToBase64String(digest);
    }

    private static string JsonLiteral(string value)
    {
        string escaped = value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t");
        return "\"" + escaped + "\"";
    }
}
