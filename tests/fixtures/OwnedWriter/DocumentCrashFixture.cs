using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;

internal static class DocumentCrashFixture
{
    internal static int Run(string[] args)
    {
        if (args is not [var root, var pointValue, var report] || !Enum.TryParse<SecretPreparationPoint>(pointValue, out var point)) return 2;
        OwnedFixtureEvidence.RequirePath(root); OwnedFixtureEvidence.RequirePath(report);
        try
        {
            var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
            var database = StateDatabase.CreateFresh(instance);
            using var lease = InstanceLease.Acquire(instance, new());
            var index = new StartupJournalRecovery(instance, new(), database,
                new(Path.Combine(root, "host"), Path.Combine(instance.RuntimeRoot, "extensions"))).Recover(lease);
            var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
            var operation = Guid.NewGuid(); var subject = Guid.NewGuid();
            var service = new SecretDocumentService(instance, database, lease, resources, index, new());
            _ = service.WriteAsync(Guid.NewGuid(), "fixture:editor", "fixture-plugin", "tasks", 0,
                "{\"formatVersion\":1,\"name\":\"before\"}"u8.ToArray(), [], () => { }, default).GetAwaiter().GetResult();
            var edits = new[] { "cn", "os", "skland", "skport", "kuro", "tajiduo" }
                .Select(field => new SecretDocumentEdit(subject, field, 0, SecretDisposition.Set, new SecretValue("synthetic-" + field))).ToArray();
            try
            {
                service = new(instance, database, lease, resources, index, new(), new Observer(instance, database, operation, subject, point, report));
                _ = service.WriteAsync(operation, "fixture:editor", "fixture-plugin", "tasks", 1,
                    "{\"formatVersion\":1,\"name\":\"after\"}"u8.ToArray(), edits, () => { }, default).GetAwaiter().GetResult();
            }
            finally { foreach (var edit in edits) edit.Value?.Dispose(); }
            return 1;
        }
        catch (Exception error)
        {
            if (!File.Exists(report)) OwnedFixtureEvidence.WriteReport(report, new { status = "refused", code = error.Message, errorType = error.GetType().FullName });
            return 1;
        }
    }

    private sealed class Observer(RuntimeInstance instance, StateDatabase database, Guid operationId, Guid subjectId,
        SecretPreparationPoint target, string report) : ISecretPreparationObserver
    {
        public void Reached(SecretPreparationPoint point, Guid journalId)
        {
            if (point != target) return;
            BlobReference parent;
            using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
                parent = SecretDocumentRows.Find(connection, null, operationId) ?? throw new IOException("fixture_parent_missing");
            OwnedFixtureEvidence.WriteReport(report, new { status = "faultReady", instance, operationId, subjectId, journalId, parent,
                point = point.ToString(), processId = Environment.ProcessId });
            Thread.Sleep(Timeout.Infinite);
        }
    }
}
