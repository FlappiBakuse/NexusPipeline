using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class EffectCrashTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Decisions() =>
        from kind in new[] { JournalKind.RunAttempt, JournalKind.UpdateApply, JournalKind.ExtensionApply }
        from mode in new[] { "accepted", "entered", "sealed" }
        select new object[] { kind, mode };

    [Theory(DisplayName = "Gate Decision Persists Across Owned Process Kill And Fresh Process Read")]
    [MemberData(nameof(Decisions))]
    public async Task PersistsGateDecision(JournalKind kind, string mode)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "effect-gate-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new ContractReader()).OpenOrCreate(Path.Combine(root, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        var digest = new string('a', 64);
        var journal = new RegisteredJournal(Guid.NewGuid(), Guid.NewGuid(), kind, JournalPhase.Prepared, 1,
            $"blobs/plans/{digest}.json", digest, ["fixture:gate-crash"]);
        var input = Path.Combine(root, "acceptance-input.json");
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new { runtimeRoot = instance.RuntimeRoot, journal }));
        using var writer = new OwnedCrashProcess();
        var readyPath = Path.Combine(root, "decision-ready.json");
        var identity = writer.Start(["effect-gate", mode, input, readyPath], root, instance.InstanceId, journal.JournalId,
            Path.Combine(root, "writer-identity.json"));
        var ready = await OwnedCrashProcess.ReadReportAsync(readyPath);
        Assert.Equal("faultReady", ready.GetProperty("status").GetString());
        Assert.Equal(identity.ProcessId, ready.GetProperty("processId").GetInt32());
        var expected = mode switch { "entered" => EffectGateState.Entered, "sealed" => EffectGateState.Sealed, _ => EffectGateState.Unentered };
        var before = ready.GetProperty("gate").Deserialize<EffectGateSnapshot>()!;
        Assert.Equal(expected, before.State);
        Assert.Equal(instance.InstanceId, before.InstanceId);
        Assert.Equal(journal.JournalId, before.JournalId);
        Assert.Equal(journal.OperationId, before.OperationId);
        Assert.Equal(journal.Kind, before.Kind);
        Assert.Equal(journal.PlanDigest, before.PlanDigest);
        var stopped = await writer.StopAsync();
        Assert.True(stopped.CompleteJobStopped);
        Assert.Equal(0u, stopped.Accounting.ActiveProcesses);
        var inspectInput = Path.Combine(root, "inspect-input.json");
        File.WriteAllBytes(inspectInput, JsonSerializer.SerializeToUtf8Bytes(new
        {
            runtimeRoot = instance.RuntimeRoot, journal,
            stoppedWriter = new { completeJobStopped = stopped.CompleteJobStopped, identity = stopped.Identity, family = stopped.Family, accounting = stopped.Accounting }
        }));
        using var reader = new OwnedCrashProcess();
        var report = Path.Combine(root, "inspection.json");
        var readerIdentity = reader.Start(["effect-gate", "inspect", inspectInput, report], root, instance.InstanceId, journal.JournalId,
            Path.Combine(root, "reader-identity.json"));
        var result = await OwnedCrashProcess.ReadReportAsync(report);
        Assert.Equal(0, await reader.AwaitExitAsync());
        var readerStopped = await reader.StopAsync();
        Assert.Equal("inspected", result.GetProperty("status").GetString());
        Assert.Equal(readerIdentity.ProcessId, result.GetProperty("processId").GetInt32());
        var observed = result.GetProperty("gate").Deserialize<EffectGateSnapshot>()!;
        Assert.Equal(before, observed);
        var reopened = StateDatabase.OpenExisting(instance, Path.Combine(root, "reopen-diagnostics"));
        var registry = new RecoveryJournalRegistry(reopened);
        var durable = registry.ReadOperationJournal(journal.OperationId);
        Assert.Equal(journal.JournalId, durable.JournalId);
        Assert.Equal(journal.PlanDigest, durable.PlanDigest);
        Assert.Equal(JournalPhase.Prepared, durable.Phase);
        var gate = new JournalEffectGate(reopened);
        Assert.Equal(observed, gate.Read(durable.JournalId));
        var decision = observed;
        if (expected == EffectGateState.Unentered)
        {
            decision = gate.SealUnentered(durable, observed.Revision);
            Assert.Equal(EffectGateState.Sealed, decision.State);
        }
        Assert.Equal("effect_gate_already_decided", Assert.Throws<IOException>(() => gate.Enter(durable, decision.Revision)).Message);
        if (decision.State == EffectGateState.Sealed) Assert.Equal(decision, gate.SealUnentered(durable, decision.Revision));
        else Assert.Equal("effect_gate_already_decided", Assert.Throws<IOException>(() => gate.SealUnentered(durable, decision.Revision)).Message);
        Assert.Equal(decision, gate.Read(durable.JournalId));
        var isolation = Assert.Single(registry.ReadSnapshot().Isolations);
        Assert.Equal(journal.JournalId, isolation.JournalId);
        Assert.Equal(Assert.Single(journal.ResourceKeys), isolation.ResourceKey);
        output.WriteLine(JsonSerializer.Serialize(new { kind, mode, stopped, readerStopped, observed, decision,
            scope = "Actual SQL effect-admission decision across process kill; no typed-plan or complete no-effect recovery approval" }));
    }
}
