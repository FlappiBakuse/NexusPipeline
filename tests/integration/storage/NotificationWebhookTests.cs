using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class NotificationWebhookTests
{
    [Theory(DisplayName = "Actual Saved Webhook Tasks Use The Five Protocols And Only Published Indexed Fixture Attachments")]
    [InlineData(NotificationWebhookKind.Feishu, false)]
    [InlineData(NotificationWebhookKind.Feishu, true)]
    [InlineData(NotificationWebhookKind.Wecom, false)]
    [InlineData(NotificationWebhookKind.Wecom, true)]
    [InlineData(NotificationWebhookKind.Slack, false)]
    [InlineData(NotificationWebhookKind.Slack, true)]
    [InlineData(NotificationWebhookKind.Discord, false)]
    [InlineData(NotificationWebhookKind.Discord, true)]
    [InlineData(NotificationWebhookKind.Generic, false)]
    [InlineData(NotificationWebhookKind.Generic, true)]
    public async Task UsesDeclaredProtocols(NotificationWebhookKind kind, bool image)
    {
        NotificationHttpSink? observed = null;
        await using var sink = new NotificationHttpSink((request, _) =>
        {
            var body = request.Target switch {
                "/feishu/auth/v3/tenant_access_token/internal" => "{\"code\":0,\"tenant_access_token\":\"fixture-token\"}",
                "/feishu/im/v1/images" => "{\"code\":0,\"data\":{\"image_key\":\"fixture-image\"}}",
                "/slack/files.getUploadURLExternal" => JsonSerializer.Serialize(new { ok = true,
                    upload_url = new Uri(observed!.Address, "upload").AbsoluteUri, file_id = "fixture-file" }),
                "/slack/files.completeUploadExternal" => "{\"ok\":true}",
                "/upload" => "ok",
                _ => kind switch { NotificationWebhookKind.Feishu => "{\"code\":0}", NotificationWebhookKind.Wecom => "{\"errcode\":0}",
                    NotificationWebhookKind.Slack => "ok", _ => "" } };
            return Task.FromResult(new NotificationSinkReply(200, body));
        }); observed = sink;
        var configuration = NotificationChannelConfiguration.Default with {
            WebhookKind = kind, ScreenshotEnabled = image, FeishuAppId = "fixture-app", SlackChannelId = "fixture-channel",
            WebhookTemplate = "{\"message\":{text},\"image\":{imageBase64},\"name\":{imageFileName},\"type\":{imageContentType}}" };
        using var fixture = new NotificationReceiptFixture(new("native webhook", "", NotificationTaskKind.Webhook, true, configuration));
        using var url = new SecretValue(new Uri(sink.Address, "hook").AbsoluteUri);
        using var signing = new SecretValue("fixture-signing-key");
        using var app = new SecretValue("fixture-app-secret"); using var slack = new SecretValue("fixture-bot-token");
        await fixture.Execute(edits: [new(NotificationSecretField.WebhookUrl, 0, SecretDisposition.Set, url),
            new(NotificationSecretField.WebhookSecret, 0, SecretDisposition.Set, signing),
            new(NotificationSecretField.FeishuAppSecret, 0, SecretDisposition.Set, app),
            new(NotificationSecretField.SlackBotToken, 0, SecretDisposition.Set, slack)],
            indexedFixtureImage: image ? Png : null);
        var receipt = fixture.Receipt;
        Assert.Equal(1, fixture.Delivery.MaterializePending());
        await using var sender = Create(fixture, sink.Address);
        var reference = Assert.Single(sender.ReadQueuedForSource(fixture.Snapshot.Reference));
        var message = new NotificationDeliveryCodec(fixture.Source.Instance, new()).Read(reference).Document.Body;
        Assert.False(string.IsNullOrWhiteSpace(message));
        var earliestSignature = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var state = await Send(fixture, sender, reference);
        var latestSignature = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.Equal(NotificationDeliveryState.Succeeded, state);
        Assert.Equal(receipt, new AttemptReceiptStore(fixture.Source.Database).Read(receipt.Facts.AttemptId));
        Assert.True(fixture.Delivery.IsSettledForRun(receipt.Facts.RunId));
        await AssertProtocol(kind, image, sink.Requests.ToArray(), message, earliestSignature, latestSignature);
    }

    private static async Task AssertProtocol(NotificationWebhookKind kind, bool image, NotificationSinkRequest[] requests,
        string message, long earliestSignature, long latestSignature)
    {
        const string feishuAuth = "/feishu/auth/v3/tenant_access_token/internal";
        const string feishuUpload = "/feishu/im/v1/images";
        const string slackTicket = "/slack/files.getUploadURLExternal";
        const string slackComplete = "/slack/files.completeUploadExternal";
        string[] targets = (kind, image) switch
        {
            (NotificationWebhookKind.Feishu, true) => ["/hook", feishuUpload, "/hook"],
            (NotificationWebhookKind.Wecom, true) => ["/hook", "/hook"],
            (NotificationWebhookKind.Slack, true) => ["/hook", slackTicket, "/upload", slackComplete],
            _ => ["/hook"]
        };
        var effects = requests.Where(item => item.Target != feishuAuth).ToArray();
        Assert.Equal(targets, effects.Select(item => item.Target));
        foreach (var request in requests)
        {
            request.Headers.TryGetValue("Authorization", out var authorization);
            Assert.Equal(request.Target switch
            {
                feishuUpload => "Bearer fixture-token",
                slackTicket or slackComplete => "Bearer fixture-bot-token",
                _ => null
            }, authorization);
            if (request.Target == feishuAuth)
            {
                Assert.Equal(NotificationWebhookKind.Feishu, kind);
                Assert.True(image);
                var authentication = ReadJson(request);
                Assert.Equal("fixture-app", authentication.GetProperty("app_id").GetString());
                Assert.Equal("fixture-app-secret", authentication.GetProperty("app_secret").GetString());
            }
        }

        var png = Png;
        var digest = Convert.ToHexStringLower(SHA256.HashData(png));
        var fileName = digest + ".png";
        JsonElement body;
        if (kind == NotificationWebhookKind.Discord && image)
        {
            var parts = await ReadMultipart(effects[0]);
            Assert.Equal(new[] { "files[0]", "payload_json" }, parts.Keys.Order(StringComparer.Ordinal));
            AssertImage(parts["files[0]"], fileName, png);
            var payload = parts["payload_json"];
            Assert.Null(payload.FileName);
            Assert.Equal("application/json", payload.MediaType);
            body = ReadJson(payload.Bytes);
        }
        else body = ReadJson(effects[0]);

        switch (kind)
        {
            case NotificationWebhookKind.Feishu:
                Assert.Equal("text", body.GetProperty("msg_type").GetString());
                Assert.Equal(message, body.GetProperty("content").GetProperty("text").GetString());
                AssertFeishuSignature(body, earliestSignature, latestSignature);
                if (image)
                {
                    var parts = await ReadMultipart(effects[1]);
                    Assert.Equal(new[] { "image", "image_type" }, parts.Keys.Order(StringComparer.Ordinal));
                    AssertImage(parts["image"], fileName, png);
                    Assert.Null(parts["image_type"].FileName);
                    Assert.Equal("message", Encoding.UTF8.GetString(parts["image_type"].Bytes));
                    var attachment = ReadJson(effects[2]);
                    Assert.Equal("image", attachment.GetProperty("msg_type").GetString());
                    Assert.Equal("fixture-image", attachment.GetProperty("content").GetProperty("image_key").GetString());
                    AssertFeishuSignature(attachment, earliestSignature, latestSignature);
                }
                break;
            case NotificationWebhookKind.Wecom:
                Assert.Equal("text", body.GetProperty("msgtype").GetString());
                Assert.Equal(message, body.GetProperty("text").GetProperty("content").GetString());
                if (image)
                {
                    var attachment = ReadJson(effects[1]);
                    Assert.Equal("image", attachment.GetProperty("msgtype").GetString());
                    var payload = attachment.GetProperty("image");
                    Assert.Equal(png, payload.GetProperty("base64").GetBytesFromBase64());
                    Assert.Equal(Convert.ToHexStringLower(MD5.HashData(png)), payload.GetProperty("md5").GetString());
                }
                break;
            case NotificationWebhookKind.Slack:
                Assert.Equal(message, body.GetProperty("text").GetString());
                if (image)
                {
                    Assert.Equal("application/x-www-form-urlencoded", RequestType(effects[1]).MediaType.Value);
                    var form = new FormReader(effects[1].Prefix).ReadForm();
                    Assert.Equal(fileName, Assert.Single(form["filename"]));
                    Assert.Equal(png.Length.ToString(CultureInfo.InvariantCulture), Assert.Single(form["length"]));
                    Assert.Equal("image/png", RequestType(effects[2]).MediaType.Value);
                    Assert.Equal(png.Length, effects[2].BodyBytes);
                    Assert.Equal(digest, effects[2].Sha256);
                    var complete = ReadJson(effects[3]);
                    Assert.Equal("fixture-channel", complete.GetProperty("channel_id").GetString());
                    var file = Assert.Single(complete.GetProperty("files").EnumerateArray());
                    Assert.Equal("fixture-file", file.GetProperty("id").GetString());
                    Assert.Equal(fileName, file.GetProperty("title").GetString());
                }
                break;
            case NotificationWebhookKind.Discord:
                Assert.Equal(message, body.GetProperty("content").GetString());
                break;
            case NotificationWebhookKind.Generic:
                Assert.Equal(message, body.GetProperty("message").GetString());
                Assert.Equal(image ? Convert.ToBase64String(png) : "", body.GetProperty("image").GetString());
                Assert.Equal(image ? fileName : "", body.GetProperty("name").GetString());
                Assert.Equal(image ? "image/png" : "", body.GetProperty("type").GetString());
                break;
        }
    }

    private static void AssertFeishuSignature(JsonElement body, long earliest, long latest)
    {
        var timestamp = Assert.IsType<string>(body.GetProperty("timestamp").GetString());
        Assert.InRange(long.Parse(timestamp, CultureInfo.InvariantCulture), earliest, latest);
        var key = Encoding.UTF8.GetBytes(timestamp + "\nfixture-signing-key");
        Assert.Equal(HMACSHA256.HashData(key, ReadOnlySpan<byte>.Empty), body.GetProperty("sign").GetBytesFromBase64());
    }

    private static MediaTypeHeaderValue RequestType(NotificationSinkRequest request) => MediaTypeHeaderValue.Parse(request.Headers["Content-Type"]);

    private static JsonElement ReadJson(NotificationSinkRequest request)
    {
        Assert.Equal("application/json", RequestType(request).MediaType.Value);
        Assert.Equal(request.BodyBytes, request.PrefixBytes.Length);
        return ReadJson(request.PrefixBytes);
    }

    private static JsonElement ReadJson(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }

    private static async Task<Dictionary<string, MultipartPart>> ReadMultipart(NotificationSinkRequest request)
    {
        var type = RequestType(request);
        Assert.Equal("multipart/form-data", type.MediaType.Value);
        Assert.Equal(request.BodyBytes, request.PrefixBytes.Length);
        using var body = new MemoryStream(request.PrefixBytes);
        var reader = new MultipartReader(HeaderUtilities.RemoveQuotes(type.Boundary).Value!, body);
        var parts = new Dictionary<string, MultipartPart>(StringComparer.Ordinal);
        while (await reader.ReadNextSectionAsync() is { } section)
        {
            var disposition = ContentDispositionHeaderValue.Parse(section.ContentDisposition);
            Assert.Equal("form-data", disposition.DispositionType.Value);
            var name = Assert.IsType<string>(HeaderUtilities.RemoveQuotes(disposition.Name).Value);
            using var contents = new MemoryStream();
            await section.Body.CopyToAsync(contents);
            var part = new MultipartPart(HeaderUtilities.RemoveQuotes(disposition.FileName).Value,
                section.ContentType is { } contentType ? MediaTypeHeaderValue.Parse(contentType).MediaType.Value : null, contents.ToArray());
            Assert.True(parts.TryAdd(name, part), name);
        }
        return parts;
    }

    private static void AssertImage(MultipartPart part, string fileName, byte[] png)
    {
        Assert.Equal(fileName, part.FileName);
        Assert.Equal("image/png", part.MediaType);
        Assert.Equal(png, part.Bytes);
    }

    private sealed record MultipartPart(string? FileName, string? MediaType, byte[] Bytes);

    [Fact(DisplayName = "A Real Http Timeout Remains Unknown And Does Not Resend Or Change The Execution Receipt")]
    public async Task KeepsUnknownTimeout()
    {
        await using var sink = new NotificationHttpSink(async (_, cancellation) =>
        { await Task.Delay(Timeout.Infinite, cancellation); return new(200, ""); });
        var config = NotificationChannelConfiguration.Default with { WebhookKind = NotificationWebhookKind.Generic,
            WebhookTemplate = "{\"text\":{text}}", TimeoutSeconds = 5 };
        using var fixture = new NotificationReceiptFixture(new("timeout", "", NotificationTaskKind.Webhook, true, config));
        using var url = new SecretValue(new Uri(sink.Address, "hook").AbsoluteUri);
        await fixture.Execute(edits: [new(NotificationSecretField.WebhookUrl, 0, SecretDisposition.Set, url)]);
        fixture.Delivery.MaterializePending();
        await using var sender = Create(fixture, sink.Address);
        var reference = Assert.Single(sender.ReadQueuedForSource(fixture.Snapshot.Reference));
        Assert.Equal(NotificationDeliveryState.Unknown, await Send(fixture, sender, reference));
        Assert.Equal(NotificationDeliveryState.Unknown, await Send(fixture, sender, reference));
        Assert.Single(sink.Requests);
        Assert.False(fixture.Delivery.IsSettledForRun(fixture.Receipt.Facts.RunId));
        Assert.Equal(fixture.Receipt, new AttemptReceiptStore(fixture.Source.Database).Read(fixture.Receipt.Facts.AttemptId));
        var delivery = new NotificationDeliveryCodec(fixture.Source.Instance, new()).Read(reference);
        var secret = Assert.Single(delivery.Document.Secrets.Where(item => item.Payload is not null)).Payload!;
        Assert.True(DeliveryReferenceIndex.IsSecretReferenced(fixture.Source.Instance, fixture.Source.Database,
            new(), secret.SecretId, secret.Digest, secret.SizeBytes));
    }

    [Fact(DisplayName = "A Real Http Acknowledgement With Sql Failure Publishes The Same Owned Result Without A Second Request")]
    public async Task ReplaysOwnedAcknowledgement()
    {
        await using var sink = new NotificationHttpSink((_, _) => Task.FromResult(new NotificationSinkReply(200, "")));
        var config = NotificationChannelConfiguration.Default with { WebhookKind = NotificationWebhookKind.Generic, WebhookTemplate = "{\"text\":{text}}" };
        using var fixture = new NotificationReceiptFixture(new("sql result", "", NotificationTaskKind.Webhook, true, config));
        using var url = new SecretValue(new Uri(sink.Address, "hook").AbsoluteUri);
        await fixture.Execute(edits: [new(NotificationSecretField.WebhookUrl, 0, SecretDisposition.Set, url)]);
        fixture.Delivery.MaterializePending();
        await using var sender = Create(fixture, sink.Address);
        var reference = Assert.Single(sender.ReadQueuedForSource(fixture.Snapshot.Reference));
        fixture.Sql("CREATE TRIGGER notifications.fail_notify_result BEFORE INSERT ON notification_delivery_transitions WHEN NEW.ordinal=2 BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        try
        {
            Assert.Equal(NotificationDeliveryState.Sending, await Send(fixture, sender, reference));
            Assert.Equal("notification_result_publication_pending", sender.FailureCode); Assert.Single(sink.Requests);
        }
        finally { fixture.Sql("DROP TRIGGER notifications.fail_notify_result;"); }
        Assert.Equal(1, sender.ReconcilePendingResults()); Assert.Equal(0, sender.ReconcilePendingResults());
        Assert.Single(sink.Requests); Assert.True(fixture.Delivery.IsSettledForRun(fixture.Receipt.Facts.RunId));
    }

    internal static NotificationSender Create(NotificationReceiptFixture fixture, Uri sink) => new(fixture.Source.Instance,
        fixture.Source.Database, new(), fixture.Source.Lease,
        new(fixture.Source.Instance, fixture.Source.Database, fixture.Source.Lease, fixture.Base.Resources, fixture.Base.Index, new()),
        NotificationWebhookEndpoints.LocalSink(new Uri(sink.GetLeftPart(UriPartial.Authority) + "/")));

    internal static async Task<NotificationDeliveryState> Send(NotificationReceiptFixture fixture, NotificationSender sender, BlobReference reference)
    {
        var saved = new NotificationDeliveryCodec(fixture.Source.Instance, new()).Read(reference);
        using var root = fixture.Base.Resources.TryAcquire(saved.Document.OperationId, NotificationSender.Claims).Lease;
        Assert.NotNull(root);
        return await sender.SendOwnedAsync(root, fixture.Base.Resources, reference);
    }

    internal static byte[] Png => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aJ1kAAAAASUVORK5CYII=");
}
