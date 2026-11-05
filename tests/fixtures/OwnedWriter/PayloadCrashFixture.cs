using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

internal static class PayloadCrashFixture
{
    internal static int Run(string[] args)
    {
        if (args is not [var mode, var inputPath, var point, var revisionValue, var report] ||
            mode is not ("accept" or "accept-extension" or "recover") || !long.TryParse(revisionValue, out var revision) ||
            point != "none" && (!Enum.TryParse<PayloadPersistencePoint>(point, out var parsed) || !Enum.IsDefined(parsed))) return 2;
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
            var roots = input.GetProperty("roots").Deserialize<PayloadApplyRoots>()!;
            OwnedFixtureEvidence.RequirePath(roots.HostPayloadRoot);
            OwnedFixtureEvidence.RequirePath(roots.ExtensionPayloadRoot);
            var contracts = new ContractReader();
            var instance = new InstanceRoot(contracts).OpenExisting(runtime);
            using var lease = InstanceLease.Acquire(instance, contracts);
            var database = StateDatabase.OpenExisting(instance, Path.Combine(Path.GetDirectoryName(report)!, Path.GetFileNameWithoutExtension(report) + "-diagnostics"),
                new JournalStartupAdmission(instance, contracts, roots));
            var observer = new Observer(point, revision, report);
            if (mode == "accept-extension")
            {
                PreparedPayloadFixture.Accept(input, instance, database, lease, contracts, roots, observer);
                throw new IOException("fixture_acceptance_barrier_missing");
            }
            var reference = input.GetProperty("plan").Deserialize<BlobReference>()!;
            var plan = new PayloadPlanCodec(instance, contracts, new(instance, contracts), roots).Read(reference);
            object result;
            if (mode == "accept")
                result = new PayloadApplyAcceptance(instance, contracts, database, roots, observer).Accept(
                    new(plan.OperationId, "fixture-payload", new string('a', 64), "fixture.payload"), plan, DateTimeOffset.UtcNow);
            else
            {
                VerifyStopped(input, instance.InstanceId, plan.JournalId, plan.OperationId);
                var registered = new RecoveryJournalRegistry(database).ReadSnapshot().Journals.Single(item => item.JournalId == plan.JournalId);
                var ownership = new JournalRecoveryOwnership(database);
                var handler = new PreparedPayloadHandler(instance, contracts, database, roots, plan.Kind, id =>
                {
                    VerifyStopped(input, instance.InstanceId, id, plan.OperationId);
                    var actual = ownership.Observe(id);
                    return new(instance.InstanceId, id, actual.IsComplete, actual.State);
                }, observer, ownership, lease);
                using var work = handler.Prepare(registered);
                using var executing = ownership.Begin(new RecoveryJournalRegistry(database).ReadSnapshot().Journals);
                result = work.Execute();
            }
            OwnedFixtureEvidence.WriteReport(report, new { status = "completed", result = result.ToString(), processId = Environment.ProcessId,
                instanceId = instance.InstanceId, journalId = plan.JournalId, snapshot = new RecoveryJournalRegistry(database).ReadSnapshot() });
            return 0;
        }
        catch (Exception error)
        {
            if (reportValidated && !File.Exists(report))
                OwnedFixtureEvidence.WriteReport(report, new { status = "refused", type = error.GetType().FullName, code = error.Message });
            return 1;
        }
    }

    private static void VerifyStopped(JsonElement input, Guid instanceId, Guid journalId, Guid operationId)
    {
        var stopped = input.GetProperty("stoppedWriters");
        if (stopped.GetArrayLength() is < 1 or > 4) throw new IOException("payload_fixture_stop_inventory_invalid");
        foreach (var writer in stopped.EnumerateArray())
        {
            var scope = writer.GetProperty("identity").GetProperty("AttemptId").GetGuid();
            if (scope != journalId && scope != operationId) throw new IOException("fixture_writer_scope_unbound");
            OwnedFixtureEvidence.VerifyStopped(JsonSerializer.SerializeToElement(new { stoppedWriter = writer }), instanceId, scope);
        }
    }

    private sealed class Observer(string target, long targetRevision, string report) : IPayloadPersistenceObserver
    {
        public void Reached(PayloadPersistencePoint point, Guid journalId, long revision, JournalPhase phase)
        {
            if (point.ToString() != target || revision != targetRevision) return;
            OwnedFixtureEvidence.WriteReport(report, new { status = "faultReady", point = target, revision, phase = phase.ToString(), journalId, processId = Environment.ProcessId });
            Thread.Sleep(Timeout.Infinite);
        }
    }
}
