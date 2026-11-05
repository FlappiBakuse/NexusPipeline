using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class SenderOwnerTests
{
    [Fact(DisplayName = "A Real Sender Killed After The Socket Request Reopens As Unknown And Cannot Resend Or Release Its Credential Pin")]
    public async Task KeepsUnknownRequests()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sink = new NotificationHttpSink(async (_, cancellation) =>
        {
            arrived.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            return new(200, "");
        });
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "notification-sender-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var reportPath = Path.Combine(root, "sender.json");
        using var owner = new OwnedCrashProcess();
        var native = owner.Start(["notification-sender-recovery", root, sink.Address.AbsoluteUri, reportPath], root,
            Guid.NewGuid(), Guid.NewGuid(), Path.Combine(root, "native-root.json"));
        var report = await OwnedCrashProcess.ReadReportAsync(reportPath, TimeSpan.FromSeconds(30));
        Assert.Equal("senderReady", report.GetProperty("status").GetString());
        Assert.Equal(native.ProcessId, report.GetProperty("processId").GetInt32());
        var instance = report.GetProperty("instance").Deserialize<RuntimeInstance>()!;
        var source = report.GetProperty("Source").Deserialize<BlobReference>()!;
        var reference = report.GetProperty("Delivery").Deserialize<BlobReference>()!;
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
        NotificationDeliveryTransition claimed;
        var delivery = new NotificationDeliveryCodec(instance, new()).Read(reference);
        using (var connection = DatabaseConnections.Open(Path.Combine(instance.RuntimeRoot, "state", "nexus.db"), readOnly: true))
        {
            claimed = NotificationDeliveryRows.ReadState(connection, null, delivery.Document.OperationId);
            Assert.Equal(NotificationDeliveryState.Sending, claimed.State);
        }
        Assert.NotNull(claimed.Sender);
        var senderIdentity = claimed.Sender.ToProcessIdentity();
        Assert.Equal(native.ProcessId, senderIdentity.ProcessId); Assert.Equal(native.CreatedAtUtc, senderIdentity.CreatedAtUtc);
        Assert.NotEqual(WriterState.Stopped, WriterStopEvidence.ObserveProcess(senderIdentity));
        Assert.ThrowsAny<IOException>(() => { using var forbidden = InstanceLease.Acquire(instance, new()); });
        Assert.Single(sink.Requests);

        var stopped = await owner.StopAsync();
        Assert.True(stopped.CompleteJobStopped);
        Assert.Contains(stopped.Family, item => item.ProcessId == senderIdentity.ProcessId && item.CreatedAtUtc == senderIdentity.CreatedAtUtc);
        Assert.Equal(stopped.Family.Count, stopped.Family.Select(item => (item.ProcessId, item.CreatedAtUtc)).Distinct().Count());
        Assert.All(stopped.Family, item => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(item)));
        _ = await owner.AwaitExitAsync();
        Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(senderIdentity));

        using var lease = InstanceLease.Acquire(instance, new());
        var roots = new PayloadApplyRoots(Path.Combine(root, "host"), Path.Combine(instance.RuntimeRoot, "extensions"));
        var database = StateDatabase.OpenExisting(instance, Path.Combine(root, "inspection"),
            new JournalStartupAdmission(instance, new(), roots));
        var index = new StartupJournalRecovery(instance, new(), database, roots).Recover(lease);
        Assert.True(index.Current.IsReady);
        var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
        await using var reopened = new NotificationSender(instance, database, new(), lease,
            new SecretFieldService(instance, database, lease, resources, index, new()));
        using var sending = resources.TryAcquire(delivery.Document.OperationId, NotificationSender.Claims).Lease;
        Assert.NotNull(sending);
        Assert.True(reopened.ReconcileStoppedOwner(sending, resources, reference));
        Assert.False(reopened.ReconcileStoppedOwner(sending, resources, reference));
        Assert.Equal(NotificationDeliveryState.Unknown, await reopened.SendOwnedAsync(sending, resources, reference));
        Assert.Empty(reopened.ReadQueuedForSource(source)); Assert.Empty(reopened.ReadSendingForSource(source));
        Assert.Single(sink.Requests);
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        {
            var unknown = NotificationDeliveryRows.ReadState(connection, null, delivery.Document.OperationId);
            Assert.Equal(NotificationDeliveryState.Unknown, unknown.State);
            Assert.Equal(claimed.Sender, unknown.Sender); Assert.Equal(claimed.Network, unknown.Network);
            Assert.Equal(claimed.Ordinal + 1, unknown.Ordinal);
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT (SELECT COUNT(*) FROM attempt_receipts)+(SELECT COUNT(*) FROM run_records);";
            Assert.Equal(0L, count.ExecuteScalar());
        }
        var credential = Assert.Single(delivery.Document.Secrets.Where(item => item.Payload is not null)).Payload!;
        Assert.True(DeliveryReferenceIndex.IsSecretReferenced(instance, database, new(),
            credential.SecretId, credential.Digest, credential.SizeBytes));
        _ = StateDatabase.OpenExisting(instance, Path.Combine(root, "second-inspection"),
            new JournalStartupAdmission(instance, new(), roots));
        Assert.Single(sink.Requests);
    }
}
