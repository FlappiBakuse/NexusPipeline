using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class AttemptCrashTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> PersistenceBoundaries()
    {
        yield return [AttemptPersistencePoint.AcceptanceCommitted, 1L];
        yield return [AttemptPersistencePoint.JournalCreated, 1L];
        yield return [AttemptPersistencePoint.GateSealed, 1L];
        yield return [AttemptPersistencePoint.JournalFlushed, 2L];
        yield return [AttemptPersistencePoint.SqlProjectionCommitted, 2L];
        yield return [AttemptPersistencePoint.JournalFlushed, 3L];
        yield return [AttemptPersistencePoint.AttemptReceiptCommitted, 3L];
        yield return [AttemptPersistencePoint.SqlProjectionCommitted, 3L];
    }

    [Theory(DisplayName = "Actual Killed Attempt Recovery Keeps Its Four Outcomes And Never Replays Launch Or Configuration")]
    [MemberData(nameof(PersistenceBoundaries))]
    public async Task PreservesKilledOutcomes(AttemptPersistencePoint point, long revision)
    {
        var fixture = AttemptTestFixture.Create();
        BlobReference reference;
        using (var prepared = new AttemptPlanCodec(fixture.Instance, new(), fixture.Source.Blobs).Freeze(fixture.Plan)) reference = prepared.Reference;
        var logBefore = File.ReadAllBytes(fixture.Plan.InitialLogs[0].Path);
        var input = new JsonObject { ["runtimeRoot"] = fixture.Instance.RuntimeRoot, ["plan"] = JsonSerializer.SerializeToNode(reference), ["stoppedWriters"] = new JsonArray() };
        var stopped = new List<OwnedCrashStop>();
        var inputPath = WriteInput(fixture, input, stopped, "acceptance-input.json");
        stopped.Add(await Interrupt(fixture, "accept", inputPath, AttemptPersistencePoint.AcceptanceCommitted, 1, "acceptance"));
        if (point != AttemptPersistencePoint.AcceptanceCommitted)
        {
            inputPath = WriteInput(fixture, input, stopped, "recovery-fault-input.json");
            stopped.Add(await Interrupt(fixture, "recover", inputPath, point, revision, "recovery-fault"));
        }
        inputPath = WriteInput(fixture, input, stopped, "complete-input.json");
        var first = await Recover(fixture, inputPath, "first");
        Assert.Equal((revision == 3 ? JournalRecoveryResult.AlreadyCompleted : JournalRecoveryResult.NotExecuted).ToString(), first.GetProperty("result").GetString());
        var snapshot = new RecoveryJournalRegistry(fixture.Database).ReadSnapshot();
        Assert.Equal(JournalPhase.Committed, Assert.Single(snapshot.Journals).Phase);
        Assert.Empty(snapshot.Isolations);
        Assert.Empty(snapshot.FileEffects);
        Assert.Equal(EffectGateState.Sealed, new JournalEffectGate(fixture.Database).Read(fixture.JournalId).State);
        var receipt = new AttemptReceiptStore(fixture.Database).Read(Guid.Parse(fixture.Plan.AttemptId));
        Assert.NotNull(receipt);
        Assert.Equal(new AttemptFacts(Guid.Parse(fixture.Plan.RunId), Guid.Parse(fixture.Plan.AttemptId), 1, EngineOutcome.NotStarted,
            BusinessOutcome.Unverified, LifecycleOutcome.Blocked, RecoveryOutcome.NotRequired, "attempt_not_started_before_interruption"), receipt.Facts);
        var actual = new FileTreeScanner(SnapshotBudget.Default).Capture(fixture.Configuration.TargetPath, default);
        Assert.Equal(fixture.Configuration.Before.Snapshot.Entries, actual.Entries);
        Assert.Equal(fixture.Configuration.Before.OriginalObjects, PhysicalTreeIdentity.Capture(fixture.Configuration.TargetPath, actual));
        Assert.Equal(logBefore, File.ReadAllBytes(fixture.Plan.InitialLogs[0].Path));
        Assert.False(Directory.Exists(Path.Combine(fixture.Instance.RuntimeRoot, fixture.Plan.OutputDirectory)));
        Assert.False(Directory.Exists(Path.Combine(fixture.Instance.RuntimeRoot, fixture.Plan.LogDirectory)));
        var beforeRepeat = JournalBytes(fixture);
        var second = await Recover(fixture, inputPath, "second");
        Assert.Equal(JournalRecoveryResult.AlreadyCompleted.ToString(), second.GetProperty("result").GetString());
        Assert.Equal(receipt, new AttemptReceiptStore(fixture.Database).Read(Guid.Parse(fixture.Plan.AttemptId)));
        Assert.Equal(beforeRepeat, JournalBytes(fixture));
        Assert.Equal(snapshot.Revision, new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Revision);
        output.WriteLine(JsonSerializer.Serialize(new { point, revision, stopped, first, second, snapshot, receipt,
            scope = "Actual owned Job kills during prepared attempt acceptance and recovery; no engine launch or applying-phase recovery approval" }));
    }

    private static async Task<OwnedCrashStop> Interrupt(AttemptTestFixture fixture, string mode, string input, AttemptPersistencePoint point, long revision, string label)
    {
        using var process = new OwnedCrashProcess();
        var report = Path.Combine(fixture.Root, label + "-report.json");
        var identity = Start(process, fixture, mode, input, point.ToString(), revision, report, label);
        var fault = await OwnedCrashProcess.ReadReportAsync(report);
        Assert.Equal("faultReady", fault.GetProperty("status").GetString());
        Assert.Equal(identity.ProcessId, fault.GetProperty("processId").GetInt32());
        Assert.Equal(point.ToString(), fault.GetProperty("point").GetString());
        Assert.Equal(revision, fault.GetProperty("revision").GetInt64());
        return await process.StopAsync();
    }

    private static async Task<JsonElement> Recover(AttemptTestFixture fixture, string input, string label)
    {
        using var process = new OwnedCrashProcess();
        var report = Path.Combine(fixture.Root, label + "-report.json");
        Start(process, fixture, "recover", input, "none", 0, report, label);
        var result = await OwnedCrashProcess.ReadReportAsync(report);
        Assert.Equal(0, await process.AwaitExitAsync());
        var stopped = await process.StopAsync();
        File.WriteAllBytes(Path.Combine(fixture.Root, label + "-processes.json"), JsonSerializer.SerializeToUtf8Bytes(stopped));
        Assert.Equal("completed", result.GetProperty("status").GetString());
        return result;
    }

    private static NexusPipeline.Platform.Processes.OwnedWorkerIdentity Start(OwnedCrashProcess process, AttemptTestFixture fixture,
        string mode, string input, string point, long revision, string report, string label) => process.Start(
            ["run-attempt", mode, input, point, revision.ToString(CultureInfo.InvariantCulture), report], fixture.Root,
            fixture.Instance.InstanceId, fixture.JournalId, Path.Combine(fixture.Root, label + "-identity.json"));

    private static string WriteInput(AttemptTestFixture fixture, JsonObject input, IReadOnlyList<OwnedCrashStop> stopped, string filename)
    {
        input["stoppedWriters"] = new JsonArray(stopped.Select(item => JsonSerializer.SerializeToNode(new
            { completeJobStopped = item.CompleteJobStopped, identity = item.Identity, family = item.Family, accounting = item.Accounting })).ToArray());
        var path = Path.Combine(fixture.Root, filename);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(input));
        return path;
    }

    private static string[] JournalBytes(AttemptTestFixture fixture) => Directory.GetFiles(Path.Combine(fixture.Instance.RuntimeRoot, "journals"), "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(fixture.Instance.RuntimeRoot, path) + ":" +
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
}
