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
        var stopped = await writer.StopAsync();
        var inspectInput = Path.Combine(root, "inspect-input.json");
        File.WriteAllBytes(inspectInput, JsonSerializer.SerializeToUtf8Bytes(new
        {
            runtimeRoot = instance.RuntimeRoot, journal,
            stoppedWriter = new { completeJobStopped = stopped.CompleteJobStopped, identity = stopped.Identity, family = stopped.Family, accounting = stopped.Accounting }
        }));
        var expected = mode switch { "entered" => EffectGateState.Entered, "sealed" => EffectGateState.Sealed, _ => EffectGateState.Unentered };
        var observed = new List<EffectGateSnapshot>();
        for (var index = 0; index < 2; index++)
        {
            using var reader = new OwnedCrashProcess();
            var report = Path.Combine(root, $"inspection-{index}.json");
            reader.Start(["effect-gate", "inspect", inspectInput, report], root, instance.InstanceId, journal.JournalId,
                Path.Combine(root, $"reader-{index}-identity.json"));
            var result = await OwnedCrashProcess.ReadReportAsync(report);
            Assert.Equal(0, await reader.AwaitExitAsync());
            var readerStopped = await reader.StopAsync();
            Assert.Equal("inspected", result.GetProperty("status").GetString());
            var gate = result.GetProperty("gate").Deserialize<EffectGateSnapshot>()!;
            Assert.Equal(expected, gate.State);
            Assert.Equal(mode == "accepted" ? 1 : 2, gate.Revision);
            Assert.Equal(instance.InstanceId, gate.InstanceId);
            Assert.Equal(journal.OperationId, gate.OperationId);
            Assert.Equal(journal.PlanDigest, gate.PlanDigest);
            observed.Add(gate);
            output.WriteLine(JsonSerializer.Serialize(new { index, readerStopped, result }));
        }
        Assert.Equal(observed[0], observed[1]);
        Assert.Equal(observed[0], new JournalEffectGate(database).Read(journal.JournalId));
        Assert.Single(new RecoveryJournalRegistry(database).ReadSnapshot().Isolations);
        output.WriteLine(JsonSerializer.Serialize(new { kind, mode, stopped, observed,
            scope = "Actual SQL effect-admission decision across process kill; no typed-plan or complete no-effect recovery approval" }));
    }
}
