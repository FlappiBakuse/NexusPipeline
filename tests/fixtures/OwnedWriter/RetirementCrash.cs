using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Records;
using NexusPipeline.Platform.Storage.Sqlite;

internal static class RetirementCrash
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var root, var binding, var operation, var phase, var report, var subject] ||
            subject is not ("host:history-retention" or "local:manual-retention")) return 64;
        OwnedFixtureEvidence.RequirePath(root); OwnedFixtureEvidence.RequirePath(report);
        try
        {
            var contracts = new ContractReader();
            var instance = new InstanceRoot(contracts).OpenExisting(root);
            using var lease = InstanceLease.Acquire(instance, contracts);
            var roots = new PayloadApplyRoots(AppContext.BaseDirectory, Path.Combine(root, "extensions"));
            var database = StateDatabase.OpenExisting(instance, Path.Combine(root, "diagnostics", "retirement-child"),
                new JournalStartupAdmission(instance, contracts, roots));
            var index = new StartupJournalRecovery(instance, contracts, database, roots).Recover(lease);
            var resources = new ResourceLeaseManager(new(index, TimeProvider.System));
            var inbox = new ExecutionAdmissionInbox();
            var execution = new ExecutionAdmissionService(instance, database, contracts, lease, resources, index, inbox, new(5, 6, 7));
            execution.Start(Guid.NewGuid(), "retirement-execution", new(new BindingTarget(Guid.Parse(binding)),
                new AutomationDefinitionStore(database).Read().Revision, new HostSettingsStore(database).Read().Revision), DateTimeOffset.UtcNow);
            var consumer = new ExecutionRuntimeConsumer(instance, database, contracts, lease, resources, index, inbox);
            var receipt = await consumer.RunNextAsync() ?? throw new IOException("retirement_fixture_execution_missing");
            new ExecutionRecordProjection(instance, database, contracts, lease, new(instance, database, contracts)).OnCommitted(receipt);
            var observer = new Observer(phase, report, receipt.Facts.RunId);
            var service = new ContentRetirementService(instance, database, contracts, lease, resources, index, observer);
            var page = service.RetireLogsPage(Guid.Parse(operation), subject, receipt.Facts.RunId)
                ?? throw new IOException("retirement_fixture_page_missing");
            Observer.Stop(report, page.CleanupJournalId, receipt.Facts.RunId);
            return 0;
        }
        catch (Exception error)
        {
            if (!File.Exists(report)) OwnedFixtureEvidence.WriteReport(report, new { status = "refused", code = error.Message, type = error.GetType().FullName });
            return 1;
        }
    }

    private sealed class Observer(string phase, string report, Guid runId) : ICleanupPersistenceObserver
    {
        public void Reached(CleanupPersistencePoint point, Guid journalId, long revision, JournalPhase journalPhase)
        {
            if (phase == point.ToString()) Stop(report, journalId, runId);
        }

        internal static void Stop(string report, Guid journal, Guid runId)
        {
            using (var output = new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
            {
                output.Write(JsonSerializer.SerializeToUtf8Bytes(new { status = "faultReady", processId = Environment.ProcessId, journalId = journal, runId }));
                output.Flush(flushToDisk: true);
            }
            Thread.Sleep(Timeout.Infinite);
        }
    }
}
