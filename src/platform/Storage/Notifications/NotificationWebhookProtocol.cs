using System.Runtime.Versioning;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Notifications;

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
