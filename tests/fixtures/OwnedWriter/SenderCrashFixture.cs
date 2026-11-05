using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;

internal static class SenderCrashFixture
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var root, var sinkText, var report] || !Uri.TryCreate(sinkText, UriKind.Absolute, out var sink) ||
            sink.Scheme != "http" || sink.Host != "127.0.0.1") return 2;
        OwnedFixtureEvidence.RequirePath(root); OwnedFixtureEvidence.RequirePath(report);
        try
        {
            var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
            var database = StateDatabase.CreateFresh(instance);
            using var lease = InstanceLease.Acquire(instance, new());
            var roots = new PayloadApplyRoots(Path.Combine(root, "host"), Path.Combine(instance.RuntimeRoot, "extensions"));
            var index = new StartupJournalRecovery(instance, new(), database, roots).Recover(lease);
            var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
            var taskId = Guid.NewGuid(); var operation = Guid.NewGuid();
            var catalog = new NotificationCatalogService(instance, database, lease, resources, index, new());
            var configuration = NotificationChannelConfiguration.Default with {
                WebhookKind = NotificationWebhookKind.Generic, WebhookTemplate = "{\"text\":{text}}", TimeoutSeconds = 120 };
            using (var url = new SecretValue(sink.AbsoluteUri))
                await catalog.SaveAsync(Guid.NewGuid(), "local:sender-owner", 0, taskId, 0,
                    new("actual owner", "", NotificationTaskKind.Webhook, true, configuration),
                    [new(NotificationSecretField.WebhookUrl, 0, SecretDisposition.Set, url)]);
            using var rootLease = resources.TryAcquire(operation, TaskTestService.AdmissionClaims).Lease ??
                throw new IOException("sender_fixture_admission_blocked");
            using var queue = resources.RegisterNotificationQueue(rootLease);
            var saved = new TaskTestService(instance, database, new(), lease)
                .EnqueueOwned(rootLease, resources, queue, operation, "local:sender-owner", 1, taskId, 1);
            rootLease.Dispose();
            var delivery = new NotificationDeliveryCodec(instance, new()).Read(saved.Delivery);
            await using var sender = new NotificationSender(instance, database, new(), lease,
                new SecretFieldService(instance, database, lease, resources, index, new()));
            using var sendingRoot = resources.TryContinueQueue(queue, delivery.Document.OperationId).Lease ??
                throw new IOException("sender_fixture_send_blocked");
            OwnedFixtureEvidence.WriteReport(report, new { status = "senderReady", instance, saved.Source, saved.Delivery,
                processId = Environment.ProcessId });
            _ = await sender.SendOwnedAsync(sendingRoot, resources, saved.Delivery);
            return 1;
        }
        catch (Exception error)
        {
            if (!File.Exists(report)) OwnedFixtureEvidence.WriteReport(report,
                new { status = "refused", errorType = error.GetType().FullName });
            return 1;
        }
    }
}
