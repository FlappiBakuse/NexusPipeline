using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
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
        var state = await Send(fixture, sender, reference);
        Assert.Equal(NotificationDeliveryState.Succeeded, state);
        Assert.Equal(receipt, new AttemptReceiptStore(fixture.Source.Database).Read(receipt.Facts.AttemptId));
        Assert.True(fixture.Delivery.IsSettledForRun(receipt.Facts.RunId));
        var expected = !image || kind is NotificationWebhookKind.Discord or NotificationWebhookKind.Generic ? 1 :
            kind == NotificationWebhookKind.Wecom ? 2 : 4;
        Assert.Equal(expected, sink.Requests.Count);
        var hook = sink.Requests.First(item => item.Target == "/hook");
        Assert.Contains(kind == NotificationWebhookKind.Discord && image ? "multipart/form-data" : "application/json", hook.Headers["Content-Type"]);
        Assert.Contains("NexusPipeline", hook.Prefix);
        if (kind == NotificationWebhookKind.Feishu) { Assert.Contains("\"timestamp\"", hook.Prefix); Assert.Contains("\"sign\"", hook.Prefix); }
        if (image && kind == NotificationWebhookKind.Slack) Assert.Contains(sink.Requests, item =>
            item.Target == "/slack/files.getUploadURLExternal" && item.Headers["Authorization"] == "Bearer fixture-bot-token");
    }

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
