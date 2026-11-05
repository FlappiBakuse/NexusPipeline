using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Records;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RetirementNotificationTests
{
    [Theory(DisplayName = "An Actual Socket Sending Or Unknown Delivery Cannot Retire Its Original Execution Content")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeepsUnsettledDelivery(bool unknown)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sink = new NotificationHttpSink(async (_, cancellation) =>
        {
            arrived.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            return new(200, "");
        });
        var input = new NotificationTaskInput("retirement socket", "", NotificationTaskKind.Webhook, true,
            NotificationChannelConfiguration.Default with {
                WebhookKind = NotificationWebhookKind.Generic, WebhookTemplate = "{\"text\":{text}}", TimeoutSeconds = 120 });
        using var fixture = new NotificationReceiptFixture(input);
        using (var url = new SecretValue(sink.Address.AbsoluteUri))
            await fixture.Execute(edits: [new(NotificationSecretField.WebhookUrl, 0, SecretDisposition.Set, url)]);
        Assert.Equal(1, fixture.Delivery.MaterializePending());
        var source = fixture.Source;
        await using var sender = new NotificationSender(source.Instance, source.Database, new(), source.Lease,
            new SecretFieldService(source.Instance, source.Database, source.Lease, fixture.Base.Resources, fixture.Base.Index, new()));
        var reference = Assert.Single(sender.ReadQueuedForSource(fixture.Snapshot.Reference));
        var delivery = new NotificationDeliveryCodec(source.Instance, new()).Read(reference);
        using var root = fixture.Base.Resources.TryAcquire(delivery.Document.OperationId, NotificationSender.Claims).Lease;
        Assert.NotNull(root);
        using var cancel = new CancellationTokenSource();
        var sending = sender.SendOwnedAsync(root!, fixture.Base.Resources, reference, cancel.Token);
        try
        {
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var claimed = ReadState();
            Assert.Equal(NotificationDeliveryState.Sending, claimed.State);
            Assert.NotNull(claimed.Sender);
            Assert.Equal(Environment.ProcessId, claimed.Sender!.ToProcessIdentity().ProcessId);
            Assert.NotEqual(WriterState.Stopped, WriterStopEvidence.ObserveProcess(claimed.Sender.ToProcessIdentity()));
            if (unknown)
            {
                cancel.Cancel();
                Assert.Equal(NotificationDeliveryState.Unknown, await sending.WaitAsync(TimeSpan.FromSeconds(30)));
                Assert.Equal(NotificationDeliveryState.Unknown, ReadState().State);
                root!.Dispose();
            }
            var journal = new JournalStore(source.Instance, new()).Read(fixture.Receipt.JournalId).Export();
            var metadata = fixture.Base.Counts();
            var operation = Guid.NewGuid();
            var service = new ContentRetirementService(source.Instance, source.Database, new(),
                source.Lease, fixture.Base.Resources, fixture.Base.Index);
            Assert.ThrowsAny<IOException>(() => service.RetireLogsPage(operation, "retirement-socket", fixture.Receipt.Facts.RunId));
            Assert.Equal(metadata, fixture.Base.Counts());
            Assert.Equal(0, fixture.Base.Count("record_content_retirements"));
            Assert.Equal(journal, new JournalStore(source.Instance, new()).Read(fixture.Receipt.JournalId).Export());
            Assert.Equal(fixture.Receipt, new AttemptReceiptStore(source.Database).Read(fixture.Receipt.Facts.AttemptId));
            Assert.False(fixture.Delivery.IsSettledForRun(fixture.Receipt.Facts.RunId));
            var credential = Assert.Single(delivery.Document.Secrets.Where(item => item.Payload is not null)).Payload!;
            Assert.True(DeliveryReferenceIndex.IsSecretReferenced(source.Instance, source.Database, new(),
                credential.SecretId, credential.Digest, credential.SizeBytes));
            if (unknown)
            {
                using var replayRoot = fixture.Base.Resources.TryAcquire(delivery.Document.OperationId, NotificationSender.Claims).Lease;
                Assert.NotNull(replayRoot);
                Assert.Equal(NotificationDeliveryState.Unknown, await sender.SendOwnedAsync(replayRoot!, fixture.Base.Resources, reference));
            }
            Assert.Single(sink.Requests);
        }
        finally
        {
            cancel.Cancel();
            _ = await sending.WaitAsync(TimeSpan.FromSeconds(30));
        }

        NotificationDeliveryTransition ReadState()
        {
            using var connection = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true);
            return NotificationDeliveryRows.ReadState(connection, null, delivery.Document.OperationId);
        }
    }
}
