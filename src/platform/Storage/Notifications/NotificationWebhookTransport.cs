using System.Runtime.Versioning;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;

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
