using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

internal static class CleanupCrashFixture
{
    internal static int Run(string[] args)
    {
        if (args is not [var mode, var inputPath, var point, var revisionValue, var report] || !long.TryParse(revisionValue, out var revision)) return 2;
        var reportValidated = false;
        try
        {
            OwnedFixtureEvidence.RequirePath(inputPath);
            OwnedFixtureEvidence.RequirePath(report);
            reportValidated = true;
            using var document = StrictJson.Parse(File.ReadAllBytes(inputPath));
            var input = document.RootElement;
            var runtime = input.GetProperty("runtimeRoot").GetString()!;
            OwnedFixtureEvidence.RequirePath(runtime);
            var contracts = new ContractReader();
            var instance = new InstanceRoot(contracts).OpenExisting(runtime);
            using var lease = InstanceLease.Acquire(instance, contracts);
            var blobs = new ImmutableBlobStore(instance, contracts);
            var reference = input.GetProperty("plan").Deserialize<BlobReference>()!;
            var plan = new CleanupPlanCodec(instance, contracts, blobs).Read(reference);
            var diagnostics = Path.Combine(Path.GetDirectoryName(report)!, Path.GetFileNameWithoutExtension(report) + "-database-diagnostics");
            var database = StateDatabase.OpenExisting(instance, diagnostics);
            var observer = new Observer(point, revision, report);
            object result;
            if (mode == "execute")
            {
                new FileCleanupAcceptance(instance, contracts, database, observer).Accept(
                    new(plan.OperationId, "fixture-subject", new string('a', 64), "file.cleanup"), plan, DateTimeOffset.UtcNow);
                result = new FileCleanupRecovery(new CleanupRecoverySession(instance, plan.JournalId, contracts, database,
                    () => new(instance.InstanceId, plan.JournalId, true, WriterState.Stopped), observer)).Execute();
            }
            else if (mode == "recover")
            {
                OwnedFixtureEvidence.VerifyStopped(input, instance.InstanceId, plan.JournalId);
                var registered = new RecoveryJournalRegistry(database).ReadSnapshot().Journals.Single(item => item.JournalId == plan.JournalId);
                result = new CleanupRecoveryHandler(instance, contracts, database, id =>
                {
                    OwnedFixtureEvidence.VerifyStopped(input, instance.InstanceId, id);
                    return new(instance.InstanceId, id, true, WriterState.Stopped);
                }, observer).Prepare(registered).Execute();
            }
            else return 2;
            var snapshot = new RecoveryJournalRegistry(database).ReadSnapshot();
            OwnedFixtureEvidence.WriteReport(report, new { status = "completed", result = result.ToString(), snapshot,
                processId = Environment.ProcessId, instanceId = instance.InstanceId, journalId = plan.JournalId });
            return 0;
        }
        catch (Exception error)
        {
            if (reportValidated && !File.Exists(report))
                OwnedFixtureEvidence.WriteReport(report, new { status = "refused", errorType = error.GetType().FullName, code = error.Message });
            return 1;
        }
    }

    private sealed class Observer(string target, long targetRevision, string report) : ICleanupPersistenceObserver
    {
        public void Reached(CleanupPersistencePoint point, Guid journalId, long revision, JournalPhase phase)
        {
            if (point.ToString() != target || revision != targetRevision) return;
            OwnedFixtureEvidence.WriteReport(report, new { status = "faultReady", point = target, revision, phase = phase.ToString(), journalId, processId = Environment.ProcessId });
            Thread.Sleep(Timeout.Infinite);
        }
    }
}
