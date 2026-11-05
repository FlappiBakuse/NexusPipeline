using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;

internal static class SecretCrashFixture
{
    internal static int Run(string[] args)
    {
        if (args is not [var root, var pointValue, var report] || !Enum.TryParse<SecretPreparationPoint>(pointValue, out var target)) return 2;
        OwnedFixtureEvidence.RequirePath(root); OwnedFixtureEvidence.RequirePath(report);
        try
        {
            var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
            var database = StateDatabase.CreateFresh(instance);
            using var lease = InstanceLease.Acquire(instance, new());
            var recovery = new StartupJournalRecovery(instance, new(), database,
                new(Path.Combine(root, "host"), Path.Combine(instance.RuntimeRoot, "extensions"))).Recover(lease);
            var resources = new ResourceLeaseManager(new HostLifecycle(recovery, TimeProvider.System));
            var operation = Guid.NewGuid(); var key = new SecretFieldKey("fixture-extension", Guid.NewGuid(), "credential");
            using var mutation = resources.TryAcquire(operation, [new("secrets:fields", ResourceClaimMode.Mutation)]).Lease
                ?? throw new IOException("fixture_secret_admission_failed");
            using var value = new SecretValue("secret-crash-owned-fixture");
            var store = new SecretFieldStore(instance, database, new Observer(instance, database, key, target, report));
            store.ApplyAsync(operation, key, 0, SecretDisposition.Set, value, default).GetAwaiter().GetResult();
            return 1;
        }
        catch (Exception error)
        {
            if (!File.Exists(report)) OwnedFixtureEvidence.WriteReport(report, new { status = "refused", code = error.Message, errorType = error.GetType().FullName });
            return 1;
        }
    }

    private sealed class Observer(RuntimeInstance instance, StateDatabase database, SecretFieldKey key, SecretPreparationPoint target, string report) : ISecretPreparationObserver
    {
        public void Reached(SecretPreparationPoint point, Guid journalId)
        {
            if (point != target) return;
            var journal = new RecoveryJournalRegistry(database).ReadSnapshot().Journals.Single(value => value.JournalId == journalId);
            OwnedFixtureEvidence.WriteReport(report, new { status = "faultReady", point = point.ToString(), journalId,
                processId = Environment.ProcessId, instance, key, plan = new BlobReference(journal.PlanPath,
                    new FileInfo(Path.Combine(instance.RuntimeRoot, journal.PlanPath)).Length, journal.PlanDigest) });
            Thread.Sleep(Timeout.Infinite);
        }
    }
}
