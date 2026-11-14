using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;

internal static class PublicationCrashFixture
{
    internal static int Run(string[] args)
    {
        if (args is not [var root, var mode, var report] || mode is not ("after-accept" or "mixed" or "final-sql")) return 2;
        OwnedFixtureEvidence.RequirePath(root); OwnedFixtureEvidence.RequirePath(report);
        try
        {
            var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
            var database = StateDatabase.CreateFresh(instance);
            using var lease = InstanceLease.Acquire(instance, new());
            var index = new StartupJournalRecovery(instance, new(), database,
                new(Path.Combine(root, "host"), Path.Combine(instance.RuntimeRoot, "extensions"))).Recover(lease);
            var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
            var operationId = Guid.NewGuid(); var taskId = Guid.NewGuid();
            var observer = new Observer(instance, database, operationId, taskId, mode, report);
            var service = new NotificationCatalogService(instance, database, lease, resources, index, new(), observer);
            if (mode == "final-sql")
            {
                using var connection = DatabaseConnections.Open(database.DatabasePath, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Notifications | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TRIGGER notifications.notification_final_fault BEFORE INSERT ON notification_publication_outcomes BEGIN SELECT RAISE(ABORT,'notification_final_fault'); END;";
                command.ExecuteNonQuery();
            }
            using var url = new SecretValue("https://notification-fixture.invalid/owner");
            using var secret = new SecretValue("notification-owned-creator-secret");
            try
            {
                service.SaveAsync(operationId, "local:notification-owner-fixture", 0, taskId, 0,
                    new("owner-task", "", NotificationTaskKind.Webhook, true, NotificationChannelConfiguration.Default),
                    [new(NotificationSecretField.WebhookUrl, 0, SecretDisposition.Set, url),
                     new(NotificationSecretField.WebhookSecret, 0, SecretDisposition.Set, secret)]).GetAwaiter().GetResult();
            }
            catch (SqliteException) when (mode == "final-sql") { observer.Pause(); }
            return 1;
        }
        catch (Exception error)
        {
            if (!File.Exists(report)) OwnedFixtureEvidence.WriteReport(report, new { status = "refused", code = error.Message, errorType = error.GetType().FullName });
            return 1;
        }
    }

    private sealed class Observer(RuntimeInstance instance, StateDatabase database, Guid operationId, Guid taskId,
        string mode, string report) : ISecretPreparationObserver
    {
        public void Reached(SecretPreparationPoint point, Guid journalId)
        {
            if (mode == "after-accept" && point == SecretPreparationPoint.Accepted ||
                mode == "mixed" && point == SecretPreparationPoint.CommittedJournal) Pause();
        }

        internal void Pause()
        {
            BlobReference parent;
            using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Notifications))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT relative_path,size_bytes,sha256 FROM notifications.notification_publications WHERE operation_id=$id;";
                command.Parameters.AddWithValue("$id", operationId.ToString("D"));
                using var row = command.ExecuteReader();
                if (!row.Read()) throw new IOException("notification_fixture_parent_missing");
                parent = new(row.GetString(0), row.GetInt64(1), row.GetString(2));
            }
            OwnedFixtureEvidence.WriteReport(report, new { status = "faultReady", mode, instance, operationId, taskId,
                parent, processId = Environment.ProcessId });
            Thread.Sleep(Timeout.Infinite);
        }
    }
}
