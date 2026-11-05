using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class CleanupCrashTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> PersistenceBoundaries() => CleanupJournalTests.PersistenceBoundaries();

    [Theory(DisplayName = "Killing The Owned Cleanup Process And Recovering In A New Process Preserves Exact Receipt Semantics")]
    [MemberData(nameof(PersistenceBoundaries))]
    public async Task PreservesInterruptedReceipts(
        CleanupPersistencePoint point, long revision, JournalRecoveryResult expected)
    {
        var fixture = Create();
        using var writer = new OwnedCrashProcess();
        var report = Path.Combine(fixture.Root, "execution-fault.json");
        var identity = Start(writer, fixture, "execute", fixture.InputPath, point.ToString(), revision, report, "execution-identity.json");
        var fault = await OwnedCrashProcess.ReadReportAsync(report);
        Assert.Equal("faultReady", fault.GetProperty("status").GetString());
        Assert.Equal(identity.ProcessId, fault.GetProperty("processId").GetInt32());
        var stopped = await writer.StopAsync();
        var faultEvidence = CaptureRuntime(fixture);
        var recoveryInput = WriteStoppedInput(fixture, stopped, "recovery-input.json");
        var result = await RecoverOnce(fixture, recoveryInput, "first");
        Assert.Equal(expected.ToString(), result.GetProperty("result").GetString());
        var snapshot = new RecoveryJournalRegistry(fixture.Database).ReadSnapshot();
        Assert.Equal(expected == JournalRecoveryResult.Quarantined ? JournalPhase.Quarantined : JournalPhase.Committed, Assert.Single(snapshot.Journals).Phase);
        if (expected == JournalRecoveryResult.NotExecuted) fixture.Blobs.Verify(fixture.Plan.Items[0].Blob);
        else Assert.False(File.Exists(Path.Combine(fixture.Instance.RuntimeRoot, fixture.Plan.Items[0].Blob.RelativePath)));
        var fingerprints = Evidence(fixture);
        var repeated = await RecoverOnce(fixture, recoveryInput, "second");
        Assert.Equal(expected == JournalRecoveryResult.Quarantined ? "Quarantined" : "AlreadyCompleted", repeated.GetProperty("result").GetString());
        Assert.Equal(fingerprints, Evidence(fixture));
        Assert.Equal(snapshot.Revision, new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Revision);
        output.WriteLine(JsonSerializer.Serialize(new { point, revision, stopped, expected, fault, result, faultEvidence,
            finalEvidence = CaptureRuntime(fixture), finalSnapshot = snapshot,
            scope = "Owned Windows process interruption and new-process typed cleanup recovery; no physical power-loss claim" }));
    }

    [Theory(DisplayName = "Killing The No Effect Cleanup Recovery Process Cannot Turn A Recorded Cancellation Into Deletion")]
    [InlineData(CleanupPersistencePoint.JournalFlushed, 2)]
    [InlineData(CleanupPersistencePoint.OutboxCommitted, 2)]
    [InlineData(CleanupPersistencePoint.JournalFlushed, 3)]
    [InlineData(CleanupPersistencePoint.SqlProjectionCommitted, 3)]
    public async Task PreservesRecordedCancellation(CleanupPersistencePoint point, long revision)
    {
        var fixture = Create();
        using var accepting = new OwnedCrashProcess();
        var acceptingReport = Path.Combine(fixture.Root, "accepting-fault.json");
        Start(accepting, fixture, "execute", fixture.InputPath, nameof(CleanupPersistencePoint.AcceptanceCommitted), 1, acceptingReport, "accepting-identity.json");
        Assert.Equal("faultReady", (await OwnedCrashProcess.ReadReportAsync(acceptingReport)).GetProperty("status").GetString());
        var accepted = await accepting.StopAsync();
        var firstInput = WriteStoppedInput(fixture, accepted, "first-recovery-input.json");
        using var recovery = new OwnedCrashProcess();
        var recoveryReport = Path.Combine(fixture.Root, "recovery-fault.json");
        Start(recovery, fixture, "recover", firstInput, point.ToString(), revision, recoveryReport, "first-recovery-identity.json");
        Assert.Equal("faultReady", (await OwnedCrashProcess.ReadReportAsync(recoveryReport)).GetProperty("status").GetString());
        var stopped = await recovery.StopAsync();
        var faultEvidence = CaptureRuntime(fixture);
        var secondInput = WriteStoppedInput(fixture, stopped, "second-recovery-input.json");
        var result = await RecoverOnce(fixture, secondInput, "final");
        Assert.Equal(revision == 3 ? "AlreadyCompleted" : "NotExecuted", result.GetProperty("result").GetString());
        fixture.Blobs.Verify(fixture.Plan.Items[0].Blob);
        var snapshot = new RecoveryJournalRegistry(fixture.Database).ReadSnapshot();
        Assert.Empty(snapshot.Isolations);
        Assert.Equal(FileEffectState.NotExecuted, Assert.Single(snapshot.FileEffects).State);
        output.WriteLine(JsonSerializer.Serialize(new { point, revision, accepted, stopped, result, faultEvidence,
            finalEvidence = CaptureRuntime(fixture), finalSnapshot = snapshot }));
    }

    private static async Task<JsonElement> RecoverOnce(Fixture fixture, string input, string suffix)
    {
        using var recovery = new OwnedCrashProcess();
        var report = Path.Combine(fixture.Root, suffix + "-recovery-report.json");
        Start(recovery, fixture, "recover", input, "none", 0, report, suffix + "-recovery-identity.json");
        var result = await OwnedCrashProcess.ReadReportAsync(report);
        Assert.Equal(0, await recovery.AwaitExitAsync());
        var stopped = await recovery.StopAsync();
        File.WriteAllBytes(Path.Combine(fixture.Root, suffix + "-recovery-processes.json"), JsonSerializer.SerializeToUtf8Bytes(stopped));
        Assert.Equal("completed", result.GetProperty("status").GetString());
        return result;
    }

    private static OwnedWorkerIdentity Start(OwnedCrashProcess process, Fixture fixture, string mode, string input, string point, long revision,
        string report, string identityFile) => process.Start(["file-cleanup", mode, input, point, revision.ToString(CultureInfo.InvariantCulture), report],
            fixture.Root, fixture.Instance.InstanceId, fixture.Plan.JournalId, Path.Combine(fixture.Root, identityFile));

    private static string WriteStoppedInput(Fixture fixture, OwnedCrashStop stopped, string filename)
    {
        fixture.Input["stoppedWriter"] = JsonSerializer.SerializeToNode(new
            { completeJobStopped = stopped.CompleteJobStopped, identity = stopped.Identity, family = stopped.Family, accounting = stopped.Accounting });
        var path = Path.Combine(fixture.Root, filename);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(fixture.Input));
        return path;
    }

    private static Fixture Create()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "cleanup-crash-" + Guid.NewGuid().ToString("N"));
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(root, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        var blobs = new ImmutableBlobStore(instance, contracts);
        var source = Path.Combine(root, "source.bin");
        File.WriteAllBytes(source, [0, 255, 1, 3, 12]);
        var blob = blobs.PersistContent(source, Assert.Single(new FileTreeScanner(SnapshotBudget.Default).Capture(source, default).Entries), default);
        var codec = new CleanupPlanCodec(instance, contracts, blobs);
        var plan = codec.Capture(Guid.NewGuid(), Guid.NewGuid(), [blob]);
        var reference = codec.Persist(plan);
        var input = new JsonObject { ["runtimeRoot"] = instance.RuntimeRoot, ["plan"] = JsonSerializer.SerializeToNode(reference) };
        var inputPath = Path.Combine(root, "execution-input.json");
        File.WriteAllBytes(inputPath, JsonSerializer.SerializeToUtf8Bytes(input));
        return new(root, inputPath, input, instance, database, blobs, plan);
    }

    private static string[] Evidence(Fixture fixture) => Directory.GetFiles(Path.Combine(fixture.Instance.RuntimeRoot, "journals"), "*", SearchOption.AllDirectories)
        .Concat(Directory.GetFiles(Path.Combine(fixture.Instance.RuntimeRoot, "blobs"), "*", SearchOption.AllDirectories)).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(fixture.Instance.RuntimeRoot, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private static object CaptureRuntime(Fixture fixture)
    {
        var tree = new FileTreeScanner(SnapshotBudget.Default).Capture(fixture.Instance.RuntimeRoot, default);
        return new { fixture.Root, tree, objects = PhysicalTreeIdentity.Capture(fixture.Instance.RuntimeRoot, tree) };
    }

    private sealed record Fixture(string Root, string InputPath, JsonObject Input, RuntimeInstance Instance, StateDatabase Database,
        ImmutableBlobStore Blobs, FrozenFileCleanup Plan);
}
