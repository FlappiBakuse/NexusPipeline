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

    /// <summary>Webhook 类型白名单（v0.7.4 单源化，KN-26）：引用 AppSettings.WebhookTypes，不再独立维护副本。</summary>
    private static readonly string[] Types = AppSettings.WebhookTypes;

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
        string body = BuildBody(type, text, template);
        (string targetUrl, Dictionary<string, string> signatureHeaders) = ApplySignature(type, webhookUrl, webhookSecret);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, targetUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            foreach (KeyValuePair<string, string> header in signatureHeaders)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            using var response = await Http.SendAsync(request, cts.Token).ConfigureAwait(false);
            string responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            // v0.7.5（KN-33）：成功判定补 HTTP 状态码——此前飞书/钉钉只看 body code==0，HTTP 500 但 code==0 误判成功。
            bool ok = response.IsSuccessStatusCode
                && (type is not ("feishu" or "dingtalk") || JsonNode.Parse(responseText).Get("code").Int(-1) == 0);
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

    private static string BuildBody(string type, string text, string template)
    {
        string literal = JsonLiteral(text);
        switch (type)
        {
            case "dingtalk":
                return $"{{\"msgtype\":\"text\",\"text\":{{\"content\":{literal}}}}}";
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
                return $"{{\"msg_type\":\"text\",\"content\":{{\"text\":{literal}}}}}";
        }
    }

    /// <summary>
    /// 签名注入（v0.6.7 按官方规范修正，此前签名参数误放消息体且钉钉签名格式错误）：
    /// - 钉钉（自定义机器人加签）：timestamp 为毫秒时间戳，sign 为 HMAC-SHA256 的 Base64（URL 编码），追加到 Webhook URL 查询参数；
    /// - 飞书（自定义机器人签名校验）：timestamp 为秒级时间戳，sign 为 Base64，放入请求头 X-Lark-Request-Timestamp / X-Lark-Signature。
    /// 未配置密钥时原样返回（不附加签名）。真机验证仍待补充（需真实机器人环境）。
    /// </summary>
    private static (string Url, Dictionary<string, string> Headers) ApplySignature(string type, string url, string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return (url, new Dictionary<string, string>());
        }
        if (type == "dingtalk")
        {
            string timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            string sign = Uri.EscapeDataString(Sign(timestamp, secret));
            string separator = url.Contains('?') ? "&" : "?";
            return (url + separator + $"timestamp={timestamp}&sign={sign}", new Dictionary<string, string>());
        }
        if (type == "feishu")
        {
            string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            return (url, new Dictionary<string, string>
            {
                ["X-Lark-Request-Timestamp"] = timestamp,
                ["X-Lark-Signature"] = Sign(timestamp, secret),
            });
        }
        return (url, new Dictionary<string, string>());
    }

    /// <summary>官方签名算法：HMAC-SHA256 以「timestamp\nsecret」为密钥对空消息计算，结果 Base64。</summary>
    private static string Sign(string timestamp, string secret)
    {
        byte[] key = Encoding.UTF8.GetBytes($"{timestamp}\n{secret}");
        using var hmac = new HMACSHA256(key);
        byte[] digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(""));
        return Convert.ToBase64String(digest);
    }

    private static string JsonLiteral(string value)
    {
        // v0.7.4（KN-34）：改用 System.Text.Json 序列化字符串字面量——手写转义此前漏 \b/\f 等控制字符，
        // 含此类字符的通知文本会使 Webhook 端 JSON 解析失败。UnsafeRelaxedJsonEscaping：保留中文等非 ASCII
        // 原样输出（默认编码器会转义为 \uXXXX，与既有通知契约/测试断言不兼容）；控制字符仍正确转义。
        return System.Text.Json.JsonSerializer.Serialize(value, JsonLiteralOptions);
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonLiteralOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
