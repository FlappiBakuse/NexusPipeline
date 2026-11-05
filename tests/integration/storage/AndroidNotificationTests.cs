using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class AndroidNotificationTests
{
    [Fact(DisplayName = "Only The Actual Final Generic Png Producer Can Supply A Loopback Notification Screenshot")]
    public async Task RequiresFinalProducer()
    {
        await using var sink = new NotificationHttpSink((_, _) => Task.FromResult(new NotificationSinkReply(200, "")));
        var fixture = new AndroidRuntimeFixture(accept: false, forceClose: false, capture: true, captureMode: "rgba");
        await fixture.ExecuteAsync(async () =>
        {
            var source = fixture.Source; var task = Guid.NewGuid();
            var catalog = new NotificationCatalogService(source.Instance, source.Database, source.Lease, fixture.Base.Resources, fixture.Base.Index, new());
            using var url = new SecretValue(new Uri(sink.Address, "hook").AbsoluteUri);
            await catalog.SaveAsync(Guid.NewGuid(), "local:generic-image", 0, task, 0,
                new("actual generic image", "", NotificationTaskKind.Webhook, true, NotificationChannelConfiguration.Default with {
                    WebhookKind = NotificationWebhookKind.Generic, ScreenshotEnabled = true,
                    WebhookTemplate = "{\"image\":{imageBase64},\"text\":{text}}" }),
                [new(NotificationSecretField.WebhookUrl, 0, SecretDisposition.Set, url)]);
            var definitions = source.Definitions.Read();
            var binding = definitions.Bindings.Single(item => item.Id == source.BindingA) with { NotifyEnabled = true, NotificationTaskIds = [task] };
            var changes = new AutomationChanges(definitions.Revision, [], [], [], [binding], [], [], []);
            source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), AndroidRuntimeFixture.Subject, changes),
                changes, DateTimeOffset.UtcNow);
            _ = fixture.Service.Start(Guid.NewGuid(), AndroidRuntimeFixture.Subject,
                source.Request(new BindingTarget(source.BindingA)), DateTimeOffset.UtcNow);
            var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            var finality = new ExecutionRunFinality(source.Instance, source.Database, new());
            new ExecutionRecordProjection(source.Instance, source.Database, new(), source.Lease, finality).OnCommitted(receipt);
            new ExecutionNotificationRegistration(source.Instance, source.Database, new(), source.Lease, finality).OnCommitted(receipt);
            BlobReference sourceReference;
            using (var connection = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true))
            using (var transaction = connection.BeginTransaction(deferred: true))
            {
                sourceReference = Assert.IsType<BlobReference>(NotificationReceiptRows.Find(connection, transaction, receipt.Facts.AttemptId));
                transaction.Commit();
            }
            Assert.Equal(1, new NotificationDeliveryService(source.Instance, source.Database, new(), source.Lease).MaterializePending());
            await using var sender = new NotificationSender(source.Instance, source.Database, new(), source.Lease,
                new(source.Instance, source.Database, source.Lease, fixture.Base.Resources, fixture.Base.Index, new()),
                NotificationWebhookEndpoints.LocalSink(sink.Address));
            var delivery = Assert.Single(sender.ReadQueuedForSource(sourceReference));
            var saved = new NotificationDeliveryCodec(source.Instance, new()).Read(delivery);
            using var root = fixture.Base.Resources.TryAcquire(saved.Document.OperationId, NotificationSender.Claims).Lease;
            Assert.NotNull(root);
            Assert.Equal(NotificationDeliveryState.Succeeded, await sender.SendOwnedAsync(root!, fixture.Base.Resources, delivery));
            var request = Assert.Single(sink.Requests);
            using var packet = JsonDocument.Parse(request.Prefix);
            Assert.Equal(Convert.ToBase64String(File.ReadAllBytes(Path.Combine(fixture.Directory, "fixture-screenshot.png"))),
                packet.RootElement.GetProperty("image").GetString());
            fixture.VerifyOriginal();
        });
    }
}
