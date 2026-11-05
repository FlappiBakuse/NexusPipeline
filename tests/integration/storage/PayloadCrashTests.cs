using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class PayloadCrashTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> PersistenceBoundaries()
    {
        var cuts = new (PayloadPersistencePoint Point, long Revision)[]
        {
            (PayloadPersistencePoint.AcceptanceCommitted, 1), (PayloadPersistencePoint.JournalCreated, 1),
            (PayloadPersistencePoint.GateSealed, 1), (PayloadPersistencePoint.JournalFlushed, 2),
            (PayloadPersistencePoint.OutboxCommitted, 2), (PayloadPersistencePoint.SqlProjectionCommitted, 2),
            (PayloadPersistencePoint.JournalFlushed, 3), (PayloadPersistencePoint.SqlProjectionCommitted, 3)
        };
        foreach (var kind in new[] { JournalKind.UpdateApply, JournalKind.ExtensionApply })
            foreach (var cut in cuts) yield return [kind, cut.Point, cut.Revision];
    }

    [Theory(DisplayName = "Killed Payload Recovery Commits Only Verified No Effect Evidence Across Sql And Journal Boundaries")]
    [MemberData(nameof(PersistenceBoundaries))]
    public async Task RecoversVerifiedCancellation(
        JournalKind kind, PayloadPersistencePoint point, long revision)
    {
        if (kind == JournalKind.ExtensionApply)
        {
            await PayloadFaultScenario.RunAsync(point, revision, output);
            return;
        }
        using var fixture = PayloadTestFixture.Create(kind, PayloadAction.Upgrade);
        BlobReference reference;
        using (var prepared = fixture.Codec.Freeze(fixture.Plan)) reference = prepared.Reference;
        var input = new JsonObject { ["runtimeRoot"] = fixture.Instance.RuntimeRoot, ["roots"] = JsonSerializer.SerializeToNode(fixture.Roots),
            ["plan"] = JsonSerializer.SerializeToNode(reference), ["stoppedWriters"] = new JsonArray() };
        var stopped = new List<OwnedCrashStop>();
        var inputPath = WriteInput(fixture, input, stopped, "acceptance-input.json");
        stopped.Add(await Interrupt(fixture, "accept", inputPath, PayloadPersistencePoint.AcceptanceCommitted, 1, "acceptance"));
        if (point != PayloadPersistencePoint.AcceptanceCommitted)
        {
            inputPath = WriteInput(fixture, input, stopped, "recovery-fault-input.json");
            stopped.Add(await Interrupt(fixture, "recover", inputPath, point, revision, "recovery-fault"));
        }
        inputPath = WriteInput(fixture, input, stopped, "complete-input.json");
        var first = await Recover(fixture, inputPath, "first");
        var expected = revision == 3 ? JournalRecoveryResult.AlreadyCompleted : JournalRecoveryResult.NotExecuted;
        Assert.Equal(expected.ToString(), first.GetProperty("result").GetString());
        var snapshot = new RecoveryJournalRegistry(fixture.Database).ReadSnapshot();
        Assert.Equal(JournalPhase.Committed, Assert.Single(snapshot.Journals).Phase);
        Assert.Empty(snapshot.Isolations);
        Assert.All(snapshot.FileEffects, item => Assert.Equal(FileEffectState.NotExecuted, item.State));
        Assert.Equal(EffectGateState.Sealed, new JournalEffectGate(fixture.Database).Read(fixture.Plan.JournalId).State);
        var actual = fixture.Scanner.Capture(fixture.Plan.TargetPath, default);
        Assert.Equal(fixture.Plan.Before.Snapshot.Entries, actual.Entries);
        Assert.Equal(fixture.Plan.Before.OriginalObjects, PhysicalTreeIdentity.Capture(fixture.Plan.TargetPath, actual));
        var beforeRepeat = JournalBytes(fixture);
        var second = await Recover(fixture, inputPath, "second");
        Assert.Equal(JournalRecoveryResult.AlreadyCompleted.ToString(), second.GetProperty("result").GetString());
        Assert.Equal(beforeRepeat, JournalBytes(fixture));
        Assert.Equal(snapshot.Revision, new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Revision);
        output.WriteLine(JsonSerializer.Serialize(new { kind, point, revision, stopped, first, second, snapshot,
            scope = "Actual owned Job kills and new-process prepared payload cancellation; no payload execution, activation or applying-phase recovery approval" }));
    }

    private static async Task<OwnedCrashStop> Interrupt(PayloadTestFixture fixture, string mode, string input, PayloadPersistencePoint point, long revision, string label)
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

    private static async Task<JsonElement> Recover(PayloadTestFixture fixture, string input, string label)
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

    private static NexusPipeline.Platform.Processes.OwnedWorkerIdentity Start(OwnedCrashProcess process, PayloadTestFixture fixture,
        string mode, string input, string point, long revision, string report, string label) =>
        process.Start(["payload", mode, input, point, revision.ToString(CultureInfo.InvariantCulture), report], fixture.Root,
            fixture.Instance.InstanceId, fixture.Plan.JournalId, Path.Combine(fixture.Root, label + "-identity.json"));

    private static string WriteInput(PayloadTestFixture fixture, JsonObject input, IReadOnlyList<OwnedCrashStop> stopped, string filename)
    {
        input["stoppedWriters"] = new JsonArray(stopped.Select(item => JsonSerializer.SerializeToNode(new
            { completeJobStopped = item.CompleteJobStopped, identity = item.Identity, family = item.Family, accounting = item.Accounting })).ToArray());
        var path = Path.Combine(fixture.Root, filename);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(input));
        return path;
    }

    private static string[] JournalBytes(PayloadTestFixture fixture) => Directory.GetFiles(Path.Combine(fixture.Instance.RuntimeRoot, "journals"), "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(fixture.Instance.RuntimeRoot, path) + ":" +
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
}
