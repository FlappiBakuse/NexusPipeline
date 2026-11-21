using System.Runtime.Versioning;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;
using System.Globalization;
using System.Text.Json.Nodes;
using NexusPipeline.Platform.Serialization;
using System.Net;

namespace NexusPipeline.Platform.Storage.Notifications;

[SupportedOSPlatform("windows")]
internal sealed class NotificationWebhookTransport(NotificationWebhookEndpoints endpoints)
{
    internal async Task<NotificationTransportResult> SendAsync(NotificationDeliverySnapshot snapshot, NotificationTaskDefinition task,
        NotificationSecretMaterial material, PreparedNotificationNetwork network, NotificationAttachment? image, CancellationToken cancellation)
    {
        if (!NotificationReadiness.Configured(task, material.Has)) return new(NotificationTransportOutcome.Rejected, null);
        var target = new Uri(material.Text(NotificationSecretField.WebhookUrl), UriKind.Absolute);
        NotificationWebhookHttp.RequireTarget(target);
        var configuration = task.Configuration;
        using var client = network.CreateHttpClient();
        var http = new NotificationWebhookHttp(client);
        try
        {
            if (configuration.WebhookKind == NotificationWebhookKind.Generic)
                return NotificationWebhookProtocol.Result(configuration.WebhookKind, await http.PostAsync(target,
                    NotificationJsonContent.Generic(configuration.WebhookTemplate, snapshot.Document.Body, image), null, cancellation).ConfigureAwait(false));
            var text = NotificationWebhookProtocol.TextBody(configuration.WebhookKind, snapshot.Document.Body);
            if (configuration.WebhookKind == NotificationWebhookKind.Feishu) text = NotificationWebhookProtocol.SignFeishu(text, material);
            if (configuration.WebhookKind == NotificationWebhookKind.Discord && image is not null)
            {
                using var multipart = MultipartImage("files[0]", image);
                multipart.Add(new StringContent(text, Encoding.UTF8, "application/json"), "payload_json");
                return NotificationWebhookProtocol.Result(configuration.WebhookKind, await http.PostAsync(target, multipart, null, cancellation).ConfigureAwait(false));
            }
            var sent = NotificationWebhookProtocol.Result(configuration.WebhookKind,
                await http.PostAsync(target, NotificationJsonContent.Plain(text), null, cancellation).ConfigureAwait(false));
            if (sent.Outcome != NotificationTransportOutcome.Delivered || image is null) return sent;
            return configuration.WebhookKind switch {
                NotificationWebhookKind.Wecom => await Wecom(http, target, image, cancellation).ConfigureAwait(false),
                NotificationWebhookKind.Feishu => await Feishu(http, target, configuration, material, image, sent, cancellation).ConfigureAwait(false),
                NotificationWebhookKind.Slack => await Slack(http, configuration, material, image, sent, cancellation).ConfigureAwait(false),
                _ => sent };
        }
        catch (NotificationApiRejection error) { return new(NotificationTransportOutcome.Rejected, error.Status); }
        catch (Exception error) when (error is IOException or OperationCanceledException or HttpRequestException or JsonException)
        { return new(NotificationTransportOutcome.Unknown, null); }
    }

    private static async Task<NotificationTransportResult> Wecom(NotificationWebhookHttp http, Uri target,
        NotificationAttachment image, CancellationToken cancellation)
    {
        using var input = image.OpenRead();
        var md5 = Convert.ToHexStringLower(await MD5.HashDataAsync(input, cancellation).ConfigureAwait(false));
        var template = "{\"msgtype\":\"image\",\"image\":{\"base64\":{imageBase64},\"md5\":" + JsonSerializer.Serialize(md5) + "}}";
        return NotificationWebhookProtocol.Result(NotificationWebhookKind.Wecom,
            await http.PostAsync(target, NotificationJsonContent.Generic(template, "", image), null, cancellation).ConfigureAwait(false));
    }

    private async Task<NotificationTransportResult> Feishu(NotificationWebhookHttp http, Uri target,
        NotificationChannelConfiguration configuration, NotificationSecretMaterial material, NotificationAttachment image,
        NotificationTransportResult textResult, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(configuration.FeishuAppId) || !material.Has(NotificationSecretField.FeishuAppSecret)) return textResult;
        var authentication = JsonSerializer.Serialize(new { app_id = configuration.FeishuAppId,
            app_secret = material.Text(NotificationSecretField.FeishuAppSecret) });
        using var tokenDocument = NotificationWebhookProtocol.RequireApi(await http.PostAsync(new(endpoints.Feishu,
            "auth/v3/tenant_access_token/internal"), NotificationJsonContent.Plain(authentication), null, cancellation).ConfigureAwait(false), false);
        var token = NotificationWebhookProtocol.RequiredText(tokenDocument.RootElement, "tenant_access_token");
        using var multipart = MultipartImage("image", image);
        multipart.Add(new StringContent("message"), "image_type");
        using var upload = NotificationWebhookProtocol.RequireApi(await http.PostAsync(new(endpoints.Feishu, "im/v1/images"),
            multipart, token, cancellation).ConfigureAwait(false), false);
        var key = NotificationWebhookProtocol.RequiredText(upload.RootElement.GetProperty("data"), "image_key");
        var body = JsonSerializer.Serialize(new { msg_type = "image", content = new { image_key = key } });
        body = NotificationWebhookProtocol.SignFeishu(body, material);
        return NotificationWebhookProtocol.Result(NotificationWebhookKind.Feishu,
            await http.PostAsync(target, NotificationJsonContent.Plain(body), null, cancellation).ConfigureAwait(false));
    }

    private async Task<NotificationTransportResult> Slack(NotificationWebhookHttp http,
        NotificationChannelConfiguration configuration, NotificationSecretMaterial material, NotificationAttachment image,
        NotificationTransportResult textResult, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(configuration.SlackChannelId) || !material.Has(NotificationSecretField.SlackBotToken)) return textResult;
        var token = material.Text(NotificationSecretField.SlackBotToken);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> {
            ["filename"] = image.FileName, ["length"] = image.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["alt_txt"] = "NexusPipeline screenshot" });
        using var ticket = NotificationWebhookProtocol.RequireApi(await http.PostAsync(new(endpoints.Slack, "files.getUploadURLExternal"),
            form, token, cancellation).ConfigureAwait(false), true);
        var upload = new Uri(NotificationWebhookProtocol.RequiredText(ticket.RootElement, "upload_url"), UriKind.Absolute);
        var id = NotificationWebhookProtocol.RequiredText(ticket.RootElement, "file_id");
        using var input = new StreamContent(image.OpenRead());
        input.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
        var uploaded = await http.PostAsync(upload, input, null, cancellation).ConfigureAwait(false);
        if (!uploaded.IsSuccessful) return new(NotificationTransportOutcome.Rejected, uploaded.Status);
        var completed = JsonSerializer.Serialize(new { files = new[] { new { id, title = image.FileName } }, channel_id = configuration.SlackChannelId });
        var reply = await http.PostAsync(new(endpoints.Slack, "files.completeUploadExternal"),
            NotificationJsonContent.Plain(completed), token, cancellation).ConfigureAwait(false);
        using var result = NotificationWebhookProtocol.RequireApi(reply, true);
        return new(NotificationTransportOutcome.Delivered, reply.Status);
    }

    private static MultipartFormDataContent MultipartImage(string field, NotificationAttachment image)
    {
        var result = new MultipartFormDataContent();
        var body = new StreamContent(image.OpenRead());
        body.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
        result.Add(body, field, image.FileName);
        return result;
    }
}

internal static class NotificationWebhookProtocol
{
    internal static string TextBody(NotificationWebhookKind kind, string text)
    {
        var literal = JsonSerializer.Serialize(text);
        return kind switch {
            NotificationWebhookKind.Feishu => $"{{\"msg_type\":\"text\",\"content\":{{\"text\":{literal}}}}}",
            NotificationWebhookKind.Wecom => $"{{\"msgtype\":\"text\",\"text\":{{\"content\":{literal}}}}}",
            NotificationWebhookKind.Slack => $"{{\"text\":{literal}}}",
            NotificationWebhookKind.Discord => $"{{\"content\":{literal}}}",
            _ => throw new IOException("notification_webhook_kind_invalid") };
    }

    [SupportedOSPlatform("windows")]
    internal static string SignFeishu(string body, NotificationSecretMaterial material)
    {
        if (!material.Has(NotificationSecretField.WebhookSecret)) return body;
        var root = JsonNode.Parse(body)?.AsObject() ?? throw new IOException("notification_feishu_body_invalid");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        byte[]? key = null;
        try
        {
            material.Use(NotificationSecretField.WebhookSecret, secret => key = Encoding.UTF8.GetBytes(timestamp + "\n" + new string(secret)));
            root["timestamp"] = timestamp;
            root["sign"] = Convert.ToBase64String(HMACSHA256.HashData(key!, ReadOnlySpan<byte>.Empty));
            return root.ToJsonString();
        }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }

    internal static NotificationTransportResult Result(NotificationWebhookKind kind, NotificationHttpReply reply)
    {
        if (!reply.IsSuccessful) return new(NotificationTransportOutcome.Rejected, reply.Status);
        if (kind == NotificationWebhookKind.Generic || string.IsNullOrWhiteSpace(reply.Body) ||
            kind == NotificationWebhookKind.Slack && reply.Body.Trim() == "ok")
            return new(NotificationTransportOutcome.Delivered, reply.Status);
        try
        {
            using var document = StrictJson.Parse(Encoding.UTF8.GetBytes(reply.Body));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(NotificationTransportOutcome.Unknown, null);
            if (kind == NotificationWebhookKind.Feishu)
            {
                var code = root.TryGetProperty("code", out var value);
                var alternate = root.TryGetProperty("StatusCode", out var status);
                if (!code && !alternate) return new(NotificationTransportOutcome.Unknown, null);
                if (code && (!value.TryGetInt32(out var codeNumber) || codeNumber != 0) ||
                    alternate && (!status.TryGetInt32(out var other) || other != 0))
                    return new(NotificationTransportOutcome.Rejected, reply.Status);
            }
            if (kind == NotificationWebhookKind.Wecom)
            {
                if (!root.TryGetProperty("errcode", out var error) || error.ValueKind != JsonValueKind.Number || !error.TryGetInt32(out var wecomNumber))
                    return new(NotificationTransportOutcome.Unknown, null);
                if (wecomNumber != 0) return new(NotificationTransportOutcome.Rejected, reply.Status);
            }
            if (kind == NotificationWebhookKind.Slack)
            {
                if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return new(NotificationTransportOutcome.Unknown, null);
                if (ok.ValueKind == JsonValueKind.False) return new(NotificationTransportOutcome.Rejected, reply.Status);
            }
            return new(NotificationTransportOutcome.Delivered, reply.Status);
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidOperationException)
        { return new(NotificationTransportOutcome.Unknown, null); }
    }

    internal static JsonDocument RequireApi(NotificationHttpReply reply, bool slack)
    {
        if (!reply.IsSuccessful) throw new NotificationApiRejection(reply.Status);
        var document = StrictJson.Parse(Encoding.UTF8.GetBytes(reply.Body));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) { document.Dispose(); throw new IOException("notification_response_unverified"); }
        if (slack)
        {
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            { document.Dispose(); throw new IOException("notification_response_unverified"); }
            if (ok.ValueKind == JsonValueKind.True) return document;
        }
        else
        {
            if (!root.TryGetProperty("code", out var code) || !code.TryGetInt32(out var number))
            { document.Dispose(); throw new IOException("notification_response_unverified"); }
            if (number == 0) return document;
        }
        document.Dispose(); throw new NotificationApiRejection(reply.Status);
    }

    internal static string RequiredText(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString())) throw new IOException("notification_response_unverified");
        return value.GetString()!;
    }
}
internal sealed class NotificationApiRejection(int status) : Exception
{
    internal int Status { get; } = status;
}

internal sealed class NotificationJsonContent : HttpContent
{
    private sealed record Part(byte[] Literal, bool Image);
    private readonly IReadOnlyList<Part> _parts;
    private readonly NotificationAttachment? _image;
    private readonly long _length;

    private NotificationJsonContent(IReadOnlyList<Part> parts, NotificationAttachment? image)
    {
        _parts = parts; _image = image;
        _length = parts.Sum(part => part.Image ? checked(((image!.SizeBytes + 2) / 3) * 4) : part.Literal.LongLength);
        Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
    }

    internal static NotificationJsonContent Plain(string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        using var parsed = StrictJson.Parse(bytes);
        return new([new(bytes, false)], null);
    }

    internal static NotificationJsonContent Generic(string template, string text, NotificationAttachment? image)
    {
        var parts = new List<Part>(); var literal = new StringBuilder();
        void Flush()
        {
            if (literal.Length == 0) return;
            parts.Add(new(Encoding.UTF8.GetBytes(literal.ToString()), false)); literal.Clear();
        }
        var values = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["{text}"] = JsonSerializer.Serialize(text),
            ["{imageFileName}"] = JsonSerializer.Serialize(image?.FileName ?? ""),
            ["{imageContentType}"] = JsonSerializer.Serialize(image?.ContentType ?? "") };
        var tokens = values.Keys.Concat(["{imageBase64}", "{imageDataUri}"]).ToArray();
        long control = 0;
        for (var offset = 0; offset < template.Length;)
        {
            var token = tokens.FirstOrDefault(key => template.AsSpan(offset).StartsWith(key, StringComparison.Ordinal));
            if (token is null) { literal.Append(template[offset++]); control++; }
            else
            {
                offset += token.Length;
                if (token is "{imageBase64}" or "{imageDataUri}")
                {
                    literal.Append('"');
                    if (image is not null)
                    {
                        if (token == "{imageDataUri}") literal.Append("data:" + image.ContentType + ";base64,");
                        Flush(); parts.Add(new([], true));
                    }
                    literal.Append('"'); control += 100;
                }
                else { literal.Append(values[token]); control += values[token].Length; }
            }
            if (control > StrictJson.MaximumBytes) throw new IOException("notification_template_payload_limit");
        }
        Flush();
        var skeleton = parts.Where(part => !part.Image).SelectMany(part => part.Literal).ToArray();
        using var parsed = StrictJson.Parse(skeleton);
        return new(parts, image);
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        WriteAsync(stream, CancellationToken.None);
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
        WriteAsync(stream, cancellationToken);
    protected override bool TryComputeLength(out long length) { length = _length; return true; }

    private async Task WriteAsync(Stream stream, CancellationToken cancellation)
    {
        foreach (var part in _parts)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!part.Image) await stream.WriteAsync(part.Literal, cancellation).ConfigureAwait(false);
            else
            {
                using var input = _image!.OpenRead();
                using var transform = new ToBase64Transform();
                using var output = new CryptoStream(stream, transform, CryptoStreamMode.Write, leaveOpen: true);
                await input.CopyToAsync(output, 65536, cancellation).ConfigureAwait(false);
                await output.FlushFinalBlockAsync(cancellation).ConfigureAwait(false);
            }
        }
    }
}
