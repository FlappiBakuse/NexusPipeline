using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;

internal static class AttemptCrashFixture
{
    internal static int Run(string[] args)
    {
        if (args is not [var mode, var inputPath, var point, var revisionValue, var report] || mode is not ("accept" or "recover") ||
            !long.TryParse(revisionValue, out var revision) || point != "none" && (!Enum.TryParse<AttemptPersistencePoint>(point, out var parsed) || !Enum.IsDefined(parsed))) return 2;
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
            var reference = input.GetProperty("plan").Deserialize<BlobReference>()!;
            var plan = new AttemptPlanCodec(instance, contracts, new(instance, contracts)).Read(reference).ReadWire();
            var journalId = Guid.Parse(plan.JournalId);
            var database = StateDatabase.OpenExisting(instance, Path.Combine(Path.GetDirectoryName(report)!, Path.GetFileNameWithoutExtension(report) + "-diagnostics"));
            var observer = new Observer(point, revision, report);
            object result;
            if (mode == "accept")
                result = new RunAttemptAcceptance(instance, contracts, database, observer).Accept(
                    new(Guid.Parse(plan.OperationId), "fixture-attempt", new string('b', 64), "fixture.attempt"), plan, DateTimeOffset.UtcNow);
            else
            {
                VerifyStopped(input, instance.InstanceId, journalId);
                var registered = new RecoveryJournalRegistry(database).ReadSnapshot().Journals.Single(item => item.JournalId == journalId);
                result = new PreparedRecoveryHandler(instance, contracts, database, id =>
                {
                    VerifyStopped(input, instance.InstanceId, id);
                    return new(instance.InstanceId, id, true, WriterState.Stopped);
                }, observer).Prepare(registered).Execute();
            }
            OwnedFixtureEvidence.WriteReport(report, new { status = "completed", result = result.ToString(), processId = Environment.ProcessId,
                instanceId = instance.InstanceId, journalId, receipt = new AttemptReceiptStore(database).Read(Guid.Parse(plan.AttemptId)),
                snapshot = new RecoveryJournalRegistry(database).ReadSnapshot() });
            return 0;
        }
        catch (Exception error)
        {
            if (reportValidated && !File.Exists(report)) OwnedFixtureEvidence.WriteReport(report, new { status = "refused", type = error.GetType().FullName, code = error.Message });
            return 1;
        }
    }

    private static void VerifyStopped(JsonElement input, Guid instanceId, Guid journalId)
    {
        var stopped = input.GetProperty("stoppedWriters");
        if (stopped.GetArrayLength() is < 1 or > 4) throw new IOException("attempt_fixture_stop_inventory_invalid");
        foreach (var writer in stopped.EnumerateArray())
            OwnedFixtureEvidence.VerifyStopped(JsonSerializer.SerializeToElement(new { stoppedWriter = writer }), instanceId, journalId);
    }

    private sealed class Observer(string target, long targetRevision, string report) : IAttemptPersistenceObserver
    {
        public void Reached(AttemptPersistencePoint point, Guid journalId, long revision, NexusPipeline.Core.Configuration.JournalPhase phase)
        {
            if (point.ToString() != target || revision != targetRevision) return;
            OwnedFixtureEvidence.WriteReport(report, new { status = "faultReady", point = target, revision, phase = phase.ToString(), journalId, processId = Environment.ProcessId });
            Thread.Sleep(Timeout.Infinite);
        }
    }
}
