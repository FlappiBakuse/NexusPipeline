using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services.Networking;
using NexusPipeline.Services.Notification;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

internal static class WebhookSender
{
    private const string FeishuApiRoot = "https://open.feishu.cn/open-apis";
    private const string SlackApiRoot = "https://slack.com/api";
    private const string DingTalkApiRoot = "https://api.dingtalk.com/v1.0";
    private const string DingTalkLegacyApiRoot = "https://oapi.dingtalk.com";

    /// <summary>Webhook 类型白名单（单源化）：引用 AppSettings.WebhookTypes，不再独立维护副本。</summary>
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

    public static async Task<bool> SendAsync(
        AppSettings settings,
        string text,
        OutboundHttpClientProvider? outbound = null,
        NotificationImage? image = null)
    {
        string? webhookUrl = TryDecrypt(settings.WebhookUrl);
        string? webhookSecret = TryDecrypt(settings.WebhookSecret);
        string type = settings.WebhookType;
        string template = settings.WebhookTemplate;
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            Logger.Error("[错误] 未配置 Webhook 地址，无法发送。");
            return false;
        }
        if (!Types.Contains(type))
        {
            Logger.Error($"[错误] 未知 Webhook 类型「{type}」，无法发送。");
            return false;
        }
        if (type == "generic" && string.IsNullOrWhiteSpace(template))
        {
            Logger.Error("[错误] webhook_type=generic 但未配置 webhook_template，无法发送。");
            return false;
        }

        (string targetUrl, Dictionary<string, string> signatureHeaders) = ApplySignature(type, webhookUrl, webhookSecret);
        if (image is not null)
        {
            switch (type)
            {
                case "discord":
                    return await SendDiscordWithImageAsync(settings, targetUrl, signatureHeaders, BuildBody(type, text, template), image, outbound).ConfigureAwait(false);
                case "wecom":
                    return await SendWeComWithImageAsync(settings, targetUrl, signatureHeaders, BuildBody(type, text, template), image, outbound).ConfigureAwait(false);
                case "feishu":
                    return await SendFeishuWithImageAsync(settings, targetUrl, signatureHeaders, text, image, webhookSecret, outbound).ConfigureAwait(false);
                case "slack":
                    return await SendSlackWithImageAsync(settings, targetUrl, signatureHeaders, text, image, outbound).ConfigureAwait(false);
                case "dingtalk":
                    return await SendDingTalkWithImageAsync(settings, targetUrl, signatureHeaders, text, image, outbound).ConfigureAwait(false);
                case "generic":
                    return await SendJsonAsync(settings, type, targetUrl, signatureHeaders, BuildGenericBody(text, template, image), outbound).ConfigureAwait(false);
            }
        }
        string body = type == "generic"
            ? BuildGenericBody(text, template, null)
            : BuildBody(type, text, template);
        if (type == "feishu")
        {
            body = BuildSignedFeishuBody(body, webhookSecret);
        }
        return await SendJsonAsync(settings, type, targetUrl, signatureHeaders, body, outbound).ConfigureAwait(false);
    }

    private static async Task<bool> SendDiscordWithImageAsync(
        AppSettings settings,
        string targetUrl,
        Dictionary<string, string> signatureHeaders,
        string body,
        NotificationImage image,
        OutboundHttpClientProvider? outbound)
    {
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(body, Encoding.UTF8, "application/json"), "payload_json");
        var file = new ByteArrayContent(image.Data);
        file.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
        multipart.Add(file, "files[0]", image.FileName);
        return await SendRequestAsync(settings, "discord", targetUrl, signatureHeaders, multipart, outbound).ConfigureAwait(false);
    }

    private static async Task<bool> SendWeComWithImageAsync(
        AppSettings settings,
        string targetUrl,
        Dictionary<string, string> signatureHeaders,
        string textBody,
        NotificationImage image,
        OutboundHttpClientProvider? outbound)
    {
        bool textOk = await SendJsonAsync(settings, "wecom", targetUrl, signatureHeaders, textBody, outbound).ConfigureAwait(false);
        if (!textOk)
        {
            return false;
        }

        string base64 = Convert.ToBase64String(image.Data);
        string md5 = Convert.ToHexString(MD5.HashData(image.Data)).ToLowerInvariant();
        string imageBody = $"{{\"msgtype\":\"image\",\"image\":{{\"base64\":{JsonLiteral(base64)},\"md5\":{JsonLiteral(md5)}}}}}";
        bool imageOk = await SendJsonAsync(settings, "wecom", targetUrl, signatureHeaders, imageBody, outbound).ConfigureAwait(false);
        if (!imageOk)
        {
            Logger.Warn("[通知] 企业微信文字通知已发送，但图片附件发送失败。");
        }
        return imageOk;
    }

    private static async Task<bool> SendFeishuWithImageAsync(
        AppSettings settings,
        string targetUrl,
        Dictionary<string, string> signatureHeaders,
        string text,
        NotificationImage image,
        string? webhookSecret,
        OutboundHttpClientProvider? outbound)
    {
        string textBody = BuildSignedFeishuBody(BuildBody("feishu", text, ""), webhookSecret);
        bool textOk = await SendJsonAsync(settings, "feishu", targetUrl, signatureHeaders, textBody, outbound).ConfigureAwait(false);
        if (!textOk)
        {
            return false;
        }

        string? appId = settings.FeishuAppId?.Trim();
        string? appSecret = TryDecrypt(settings.FeishuAppSecret);
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(appSecret))
        {
            Logger.Warn("[通知] 飞书图片凭据未完整配置，已发送文字通知。");
            return true;
        }
        string? token = await GetFeishuTenantTokenAsync(settings, appId, appSecret, outbound).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            Logger.Warn("[通知] 飞书 tenant_access_token 获取失败，图片附件发送失败。");
            return false;
        }
        string? imageKey = await UploadFeishuImageAsync(settings, token, image, outbound).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(imageKey))
        {
            Logger.Warn("[通知] 飞书图片上传失败。");
            return false;
        }
        string imageBody = BuildSignedFeishuBody(
            $"{{\"msg_type\":\"image\",\"content\":{{\"image_key\":{JsonLiteral(imageKey)}}}}}",
            webhookSecret);
        bool imageOk = await SendJsonAsync(settings, "feishu", targetUrl, signatureHeaders, imageBody, outbound).ConfigureAwait(false);
        if (!imageOk)
        {
            Logger.Warn("[通知] 飞书文字通知已发送，但图片附件发送失败。");
        }
        return imageOk;
    }

    private static async Task<bool> SendSlackWithImageAsync(
        AppSettings settings,
        string targetUrl,
        Dictionary<string, string> signatureHeaders,
        string text,
        NotificationImage image,
        OutboundHttpClientProvider? outbound)
    {
        bool textOk = await SendJsonAsync(settings, "slack", targetUrl, signatureHeaders, BuildBody("slack", text, ""), outbound).ConfigureAwait(false);
        if (!textOk)
        {
            return false;
        }

        string? botToken = TryDecrypt(settings.SlackBotToken);
        string? channelId = settings.SlackChannelId?.Trim();
        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(channelId))
        {
            Logger.Warn("[通知] Slack 图片凭据未完整配置，已发送文字通知。");
            return true;
        }

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["filename"] = image.FileName,
            ["length"] = image.Data.Length.ToString(CultureInfo.InvariantCulture),
            ["alt_txt"] = "NexusPipeline 运行截图",
        });
        (bool ticketOk, string ticketText) = await SendApiRequestAsync(
            settings,
            "slack",
            $"{SlackApiRoot}/files.getUploadURLExternal",
            BearerHeaders(botToken),
            form,
            outbound).ConfigureAwait(false);
        if (!ticketOk || !TryReadSlackUploadTicket(ticketText, out string uploadUrl, out string fileId))
        {
            Logger.Warn("[通知] Slack 图片上传凭据获取失败。");
            return false;
        }

        using var upload = new ByteArrayContent(image.Data);
        upload.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
        (bool uploadOk, _) = await SendApiRequestAsync(settings, "slack", uploadUrl, new Dictionary<string, string>(), upload, outbound).ConfigureAwait(false);
        if (!uploadOk)
        {
            Logger.Warn("[通知] Slack 图片二进制上传失败。");
            return false;
        }

        string completeBody = JsonSerializer.Serialize(new
        {
            files = new[] { new { id = fileId, title = image.FileName } },
            channel_id = channelId,
        }, JsonOpts.Default);
        (bool completeOk, string completeText) = await SendApiRequestAsync(
            settings,
            "slack",
            $"{SlackApiRoot}/files.completeUploadExternal",
            BearerHeaders(botToken),
            new StringContent(completeBody, Encoding.UTF8, "application/json"),
            outbound).ConfigureAwait(false);
        if (!completeOk || !IsSlackSuccess(completeText))
        {
            Logger.Warn("[通知] Slack 图片发布失败。");
            return false;
        }
        Logger.Info("Slack 图片附件发送成功。");
        return true;
    }

    private static async Task<bool> SendDingTalkWithImageAsync(
        AppSettings settings,
        string targetUrl,
        Dictionary<string, string> signatureHeaders,
        string text,
        NotificationImage image,
        OutboundHttpClientProvider? outbound)
    {
        bool textOk = await SendJsonAsync(settings, "dingtalk", targetUrl, signatureHeaders, BuildBody("dingtalk", text, ""), outbound).ConfigureAwait(false);
        if (!textOk)
        {
            return false;
        }

        string? appKey = settings.DingTalkAppKey?.Trim();
        string? appSecret = TryDecrypt(settings.DingTalkAppSecret);
        string? robotCode = settings.DingTalkRobotCode?.Trim();
        string? conversationId = settings.DingTalkOpenConversationId?.Trim();
        if (string.IsNullOrWhiteSpace(appKey) || string.IsNullOrWhiteSpace(appSecret)
            || string.IsNullOrWhiteSpace(robotCode) || string.IsNullOrWhiteSpace(conversationId))
        {
            Logger.Warn("[通知] 钉钉应用机器人图片凭据未完整配置，已发送文字通知。");
            return true;
        }

        string? accessToken = await GetDingTalkAccessTokenAsync(settings, appKey, appSecret, outbound).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            Logger.Warn("[通知] 钉钉 access_token 获取失败，图片附件发送失败。");
            return false;
        }
        string? mediaId = await UploadDingTalkImageAsync(settings, accessToken, image, outbound).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            Logger.Warn("[通知] 钉钉图片上传失败。");
            return false;
        }

        string msgParam = JsonSerializer.Serialize(new { photoURL = mediaId }, JsonOpts.Default);
        string body = JsonSerializer.Serialize(new
        {
            msgKey = "sampleImageMsg",
            msgParam,
            openConversationId = conversationId,
            robotCode,
        }, JsonOpts.Default);
        (bool sendOk, string sendText) = await SendApiRequestAsync(
            settings,
            "dingtalk",
            $"{DingTalkApiRoot}/robot/groupMessages/send",
            new Dictionary<string, string> { ["x-acs-dingtalk-access-token"] = accessToken },
            new StringContent(body, Encoding.UTF8, "application/json"),
            outbound).ConfigureAwait(false);
        if (!sendOk || !IsDingTalkApiSuccess(sendText))
        {
            Logger.Warn("[通知] 钉钉应用机器人图片发送失败。");
            return false;
        }
        Logger.Info("钉钉图片附件发送成功。");
        return true;
    }

    private static async Task<string?> GetFeishuTenantTokenAsync(
        AppSettings settings,
        string appId,
        string appSecret,
        OutboundHttpClientProvider? outbound)
    {
        string body = JsonSerializer.Serialize(new { app_id = appId, app_secret = appSecret }, JsonOpts.Default);
        (bool ok, string responseText) = await SendApiRequestAsync(
            settings,
            "feishu",
            $"{FeishuApiRoot}/auth/v3/tenant_access_token/internal",
            new Dictionary<string, string>(),
            new StringContent(body, Encoding.UTF8, "application/json"),
            outbound).ConfigureAwait(false);
        if (!ok)
        {
            return null;
        }
        try
        {
            JsonNode? root = JsonNode.Parse(responseText);
            return root.Get("code").Int(-1) == 0 ? root.Get("tenant_access_token").Str() : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> UploadFeishuImageAsync(
        AppSettings settings,
        string token,
        NotificationImage image,
        OutboundHttpClientProvider? outbound)
    {
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent("message", Encoding.UTF8), "image_type");
        var file = new ByteArrayContent(image.Data);
        file.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
        multipart.Add(file, "image", image.FileName);
        (bool ok, string responseText) = await SendApiRequestAsync(
            settings,
            "feishu",
            $"{FeishuApiRoot}/im/v1/images",
            BearerHeaders(token),
            multipart,
            outbound).ConfigureAwait(false);
        if (!ok)
        {
            return null;
        }
        try
        {
            JsonNode? root = JsonNode.Parse(responseText);
            return root.Get("code").Int(-1) == 0 ? root.Get("data").Get("image_key").Str() : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> GetDingTalkAccessTokenAsync(
        AppSettings settings,
        string appKey,
        string appSecret,
        OutboundHttpClientProvider? outbound)
    {
        string body = JsonSerializer.Serialize(new { appKey, appSecret }, JsonOpts.Default);
        (bool ok, string responseText) = await SendApiRequestAsync(
            settings,
            "dingtalk",
            $"{DingTalkApiRoot}/oauth2/accessToken",
            new Dictionary<string, string>(),
            new StringContent(body, Encoding.UTF8, "application/json"),
            outbound).ConfigureAwait(false);
        if (!ok)
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(responseText).Get("accessToken").Str();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> UploadDingTalkImageAsync(
        AppSettings settings,
        string token,
        NotificationImage image,
        OutboundHttpClientProvider? outbound)
    {
        using var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(image.Data);
        file.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
        multipart.Add(file, "media", image.FileName);
        string target = $"{DingTalkLegacyApiRoot}/media/upload?access_token={Uri.EscapeDataString(token)}&type=image";
        (bool ok, string responseText) = await SendApiRequestAsync(
            settings,
            "dingtalk",
            target,
            new Dictionary<string, string>(),
            multipart,
            outbound).ConfigureAwait(false);
        if (!ok)
        {
            return null;
        }
        try
        {
            JsonNode? root = JsonNode.Parse(responseText);
            return root.Get("errcode").Int(-1) == 0 ? root.Get("media_id").Str() : null;
        }
        catch
        {
            return null;
        }
    }

    private static Task<bool> SendJsonAsync(
        AppSettings settings,
        string type,
        string targetUrl,
        Dictionary<string, string> signatureHeaders,
        string body,
        OutboundHttpClientProvider? outbound)
    {
        return SendRequestAsync(
            settings,
            type,
            targetUrl,
            signatureHeaders,
            new StringContent(body, Encoding.UTF8, "application/json"),
            outbound);
    }

    private static async Task<bool> SendRequestAsync(
        AppSettings settings,
        string type,
        string targetUrl,
        IReadOnlyDictionary<string, string> headers,
        HttpContent content,
        OutboundHttpClientProvider? outbound)
    {
        (bool httpOk, string responseText) = await SendApiRequestAsync(settings, type, targetUrl, headers, content, outbound).ConfigureAwait(false);
        bool ok = httpOk && IsResponseBodySuccessful(type, responseText);
        if (ok)
        {
            Logger.Info($"{TypeDisplay(type)} Webhook 发送成功。");
        }
        else if (httpOk)
        {
            Logger.Warn($"[警告] Webhook 返回异常：{responseText}");
        }
        return ok;
    }

    private static async Task<(bool Ok, string ResponseText)> SendApiRequestAsync(
        AppSettings settings,
        string type,
        string targetUrl,
        IReadOnlyDictionary<string, string> headers,
        HttpContent content,
        OutboundHttpClientProvider? outbound)
    {
        int timeout = settings.WebhookTimeout < 1 ? 30 : settings.WebhookTimeout;
        try
        {
            Uri target = new(targetUrl);
            using HttpClient client = (outbound ?? new OutboundHttpClientProvider(() => settings))
                .CreateClient(target, TimeSpan.FromSeconds(timeout));
            using var request = new HttpRequestMessage(HttpMethod.Post, targetUrl)
            {
                Content = content,
            };
            foreach (KeyValuePair<string, string> header in headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            using HttpResponseMessage response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);
            string responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return (response.IsSuccessStatusCode, responseText);
        }
        catch (Exception ex)
        {
            Logger.Error($"[错误] {TypeDisplay(type)} 请求失败：{ex.Message}");
            return (false, "");
        }
    }

    private static bool IsResponseBodySuccessful(string type, string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return true;
        }
        try
        {
            JsonNode? response = JsonNode.Parse(responseText);
            if (type == "feishu")
            {
                JsonNode? code = response.Get("code");
                JsonNode? statusCode = response.Get("StatusCode");
                return (code is null || code.Int(-1) == 0)
                    && (statusCode is null || statusCode.Int(-1) == 0);
            }
            if (type == "dingtalk")
            {
                JsonNode? code = response.Get("code");
                JsonNode? errorCode = response.Get("errcode");
                JsonNode? success = response.Get("success");
                return code is null && errorCode is null && success is null
                    || code.Int(0) == 0 && errorCode.Int(0) == 0 && (success is null || success.Bool());
            }
            if (type == "wecom")
            {
                return response.Get("errcode").Int(0) == 0;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildBody(string type, string text, string template)
    {
        string literal = JsonLiteral(text);
        return type switch
        {
            "dingtalk" or "wecom" => $"{{\"msgtype\":\"text\",\"text\":{{\"content\":{literal}}}}}",
            "slack" => $"{{\"text\":{literal}}}",
            "discord" => $"{{\"content\":{literal}}}",
            "generic" => string.IsNullOrWhiteSpace(template) ? literal : template.Replace("{text}", literal, StringComparison.Ordinal),
            _ => $"{{\"msg_type\":\"text\",\"content\":{{\"text\":{literal}}}}}",
        };
    }

    private static string BuildGenericBody(string text, string template, NotificationImage? image)
    {
        string base64 = image is null ? "" : Convert.ToBase64String(image.Data);
        string dataUri = image is null ? "" : $"data:{image.ContentType};base64,{base64}";
        string fileName = image?.FileName ?? "";
        string contentType = image?.ContentType ?? "";
        return template
            .Replace("{text}", JsonLiteral(text), StringComparison.Ordinal)
            .Replace("{imageBase64}", JsonLiteral(base64), StringComparison.Ordinal)
            .Replace("{imageDataUri}", JsonLiteral(dataUri), StringComparison.Ordinal)
            .Replace("{imageFileName}", JsonLiteral(fileName), StringComparison.Ordinal)
            .Replace("{imageContentType}", JsonLiteral(contentType), StringComparison.Ordinal);
    }

    private static bool TryReadSlackUploadTicket(string text, out string uploadUrl, out string fileId)
    {
        uploadUrl = "";
        fileId = "";
        try
        {
            JsonNode? root = JsonNode.Parse(text);
            if (!IsSlackSuccess(text))
            {
                return false;
            }
            uploadUrl = root.Get("upload_url").Str();
            fileId = root.Get("file_id").Str();
            return Uri.TryCreate(uploadUrl, UriKind.Absolute, out _)
                && !string.IsNullOrWhiteSpace(fileId);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSlackSuccess(string text)
    {
        try
        {
            JsonNode? root = JsonNode.Parse(text);
            return root.Get("ok").Bool();
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDingTalkApiSuccess(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }
        try
        {
            JsonNode? root = JsonNode.Parse(text);
            JsonNode? success = root.Get("success");
            JsonNode? code = root.Get("code");
            JsonNode? errorCode = root.Get("errcode");
            return (success is null || success.Bool())
                && (code is null || code.Int(0) == 0)
                && (errorCode is null || errorCode.Int(0) == 0);
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, string> BearerHeaders(string token) => new()
    {
        ["Authorization"] = $"Bearer {token}",
    };

    private static string? TryDecrypt(string stored)
    {
        return SecretStore.TryDecrypt(stored ?? "", out string? value) ? value : null;
    }

    /// <summary>
    /// 签名注入：钉钉自定义机器人使用 URL 查询参数；飞书自定义机器人使用请求体字段。
    /// 应用级图片 API 使用独立的凭据请求，不复用 Webhook 签名。
    /// </summary>
    private static (string Url, Dictionary<string, string> Headers) ApplySignature(string type, string url, string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return (url, new Dictionary<string, string>());
        }
        if (type == "dingtalk")
        {
            string timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            string sign = Uri.EscapeDataString(Sign(timestamp, secret));
            string separator = url.Contains('?') ? "&" : "?";
            return (url + separator + $"timestamp={timestamp}&sign={sign}", new Dictionary<string, string>());
        }
        return (url, new Dictionary<string, string>());
    }

    private static string BuildSignedFeishuBody(string body, string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return body;
        }
        try
        {
            if (JsonNode.Parse(body) is not JsonObject root)
            {
                return body;
            }
            string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            root["timestamp"] = timestamp;
            root["sign"] = Sign(timestamp, secret);
            return root.ToJsonString(JsonOpts.Default);
        }
        catch
        {
            return body;
        }
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
        return JsonSerializer.Serialize(value, JsonLiteralOptions);
    }

    private static readonly JsonSerializerOptions JsonLiteralOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
