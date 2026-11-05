using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

internal static class InitialCrashFixture
{
    internal static async Task<int> RunAsync(string mode, JsonElement input, RuntimeInstance instance,
        FrozenConfigurationExchange plan, StateDatabase database, string report)
    {
        var registry = new RecoveryJournalRegistry(database);
        if (mode == "accept-initial")
        {
            var reference = input.GetProperty("plan").Deserialize<BlobReference>()!;
            var registered = new RegisteredJournal(plan.JournalId, plan.OperationId, plan.Kind, JournalPhase.Prepared, 1,
                reference.RelativePath, reference.Sha256, ["configuration:fixture"]);
            var actions = new[] { ("preserve-original", FileEffectAction.PreserveOriginal), ("install-candidate", FileEffectAction.InstallCandidate),
                ("retire-candidate", FileEffectAction.RetireCandidate), ("restore-original", FileEffectAction.RestoreOriginal) };
            registry.AcceptPrepared(new(plan.OperationId, "fixture-subject", new string('a', 64), "configuration.exchange"), registered,
                actions.Select(item => new FileEffect(plan.JournalId, item.Item1, item.Item2, FileEffectState.Pending, reference.Sha256, null)).ToArray(), DateTimeOffset.UtcNow);
            OwnedFixtureEvidence.WriteReport(report, new { status = "faultReady", faultPoint = "accepted-before-initial-journal", processId = Environment.ProcessId });
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        if (mode != "recover-initial") return 2;
        var handler = new ConfigurationRecoveryHandler(instance, new ContractReader(), database, plan.Kind, id =>
        {
            OwnedFixtureEvidence.VerifyStopped(input, instance.InstanceId, id);
            return new(instance.InstanceId, id, true, WriterState.Stopped);
        });
        var result = handler.Prepare(registry.ReadSnapshot().Journals.Single(item => item.JournalId == plan.JournalId)).Execute();
        var status = result == JournalRecoveryResult.NotExecuted ? "notExecuted" : result == JournalRecoveryResult.AlreadyCompleted ? "alreadyCompleted" : result.ToString().ToLowerInvariant();
        OwnedFixtureEvidence.WriteReport(report, new { status, phase = registry.ReadSnapshot().Journals.Single(item => item.JournalId == plan.JournalId).Phase.ToString().ToLowerInvariant() });
        return 0;
    }
}
