using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;

internal static class EffectCrashFixture
{
    internal static int Run(string[] args)
    {
        if (args is not [var mode, var inputPath, var report]) return 2;
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
            var journal = input.GetProperty("journal").Deserialize<RegisteredJournal>()!;
            var database = StateDatabase.OpenExisting(instance, Path.Combine(Path.GetDirectoryName(report)!, Path.GetFileNameWithoutExtension(report) + "-diagnostics"));
            var gate = new JournalEffectGate(database);
            if (mode == "inspect")
            {
                OwnedFixtureEvidence.VerifyStopped(input, instance.InstanceId, journal.JournalId);
                var registered = new RecoveryJournalRegistry(database).ReadSnapshot().Journals.Single();
                if (registered.JournalId != journal.JournalId || registered.OperationId != journal.OperationId || registered.PlanDigest != journal.PlanDigest)
                    throw new IOException("fixture_gate_binding_changed");
                OwnedFixtureEvidence.WriteReport(report, new { status = "inspected", gate = gate.Read(journal.JournalId), processId = Environment.ProcessId });
                return 0;
            }
            if (mode is not ("accepted" or "entered" or "sealed")) return 2;
            new RecoveryJournalRegistry(database).AcceptPrepared(new(journal.OperationId, "fixture-gate-crash", journal.PlanDigest, "fixture.effect-gate"),
                journal, [], DateTimeOffset.UtcNow);
            if (mode == "entered") gate.Enter(journal, 1);
            if (mode == "sealed") gate.SealUnentered(journal, 1);
            OwnedFixtureEvidence.WriteReport(report, new { status = "faultReady", gate = gate.Read(journal.JournalId), processId = Environment.ProcessId });
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
        catch (Exception error)
        {
            if (reportValidated && !File.Exists(report))
                OwnedFixtureEvidence.WriteReport(report, new { status = "refused", type = error.GetType().FullName, code = error.Message });
            return 1;
        }
    }
}
