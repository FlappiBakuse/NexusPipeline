using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class SecretCrashTests(ITestOutputHelper output)
{
    [Theory(DisplayName = "Unknown Secret Receipt Evidence Rejects Startup Read Only And Preserves Every Original Runtime Byte")]
    [InlineData("writer")]
    [InlineData("digest")]
    [InlineData("extra")]
    public async Task PreservesUnknownReceipts(string fault)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "secret-unknown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var reportPath = Path.Combine(root, "fault.json");
        using var owner = new OwnedCrashProcess();
        _ = owner.Start(["secret-preparation", root, SecretPreparationPoint.CompletedReceipt.ToString(), reportPath], root,
            Guid.NewGuid(), Guid.NewGuid(), Path.Combine(root, "process.json"));
        var report = await OwnedCrashProcess.ReadReportAsync(reportPath);
        Assert.Equal("faultReady", report.GetProperty("status").GetString());
        var instance = report.GetProperty("instance").Deserialize<RuntimeInstance>()!;
        var stopped = await owner.StopAsync(); Assert.True(stopped.CompleteJobStopped); _ = await owner.AwaitExitAsync();
        var id = report.GetProperty("journalId").GetGuid();
        var path = Path.Combine(instance.RuntimeRoot, "secrets", "preparations", $"{id:D}.completed.json");
        if (fault == "extra") File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path)!, "unknown.json"), [18, 0, 4]);
        else
        {
            var receipt = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(path))!;
            if (fault == "writer") receipt["writerId"] = Guid.NewGuid().ToString("D");
            else receipt["candidate"]!["sha256"] = new string('a', 64);
            File.WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(receipt.ToJsonString()));
        }
        using var lease = InstanceLease.Acquire(instance, new());
        var before = RuntimeFingerprints(instance.RuntimeRoot);
        Assert.Throws<IOException>(() => StateDatabase.OpenExisting(instance, Path.Combine(root, "refused-startup"), new JournalStartupAdmission(instance, new())));
        Assert.Equal(before, RuntimeFingerprints(instance.RuntimeRoot));
    }

    [Theory(DisplayName = "Actual Secret Creator Termination Only Publishes Complete Verified Ciphertext And Never Recreates Partial Files")]
    [InlineData(SecretPreparationPoint.Accepted, JournalPhase.Committed, false)]
    [InlineData(SecretPreparationPoint.InitialJournal, JournalPhase.Committed, false)]
    [InlineData(SecretPreparationPoint.EffectEntered, JournalPhase.Quarantined, false)]
    [InlineData(SecretPreparationPoint.Applying, JournalPhase.Quarantined, false)]
    [InlineData(SecretPreparationPoint.StartedReceipt, JournalPhase.Quarantined, false)]
    [InlineData(SecretPreparationPoint.StartedOutbox, JournalPhase.Quarantined, false)]
    [InlineData(SecretPreparationPoint.CiphertextFlushed, JournalPhase.Quarantined, false)]
    [InlineData(SecretPreparationPoint.CompletedReceipt, JournalPhase.Committed, true)]
    [InlineData(SecretPreparationPoint.CompletedOutbox, JournalPhase.Committed, true)]
    [InlineData(SecretPreparationPoint.Applied, JournalPhase.Committed, true)]
    [InlineData(SecretPreparationPoint.CommittedJournal, JournalPhase.Committed, true)]
    [InlineData(SecretPreparationPoint.Published, JournalPhase.Committed, true)]
    public async Task RecoversVerifiedCiphertext(
        SecretPreparationPoint point, JournalPhase phase, bool published)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "secret-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var reportPath = Path.Combine(root, "fault.json");
        using var owner = new OwnedCrashProcess();
        var identity = owner.Start(["secret-preparation", root, point.ToString(), reportPath], root, Guid.NewGuid(), Guid.NewGuid(), Path.Combine(root, "process.json"));
        var report = await OwnedCrashProcess.ReadReportAsync(reportPath);
        Assert.Equal("faultReady", report.GetProperty("status").GetString());
        Assert.Equal(identity.ProcessId, report.GetProperty("processId").GetInt32());
        var instance = report.GetProperty("instance").Deserialize<RuntimeInstance>()!;
        var key = report.GetProperty("key").Deserialize<SecretFieldKey>()!;
        var database = StateDatabase.OpenExisting(instance, Path.Combine(root, "before-stop"));
        var id = report.GetProperty("journalId").GetGuid();
        Assert.Equal(new WriterStopObservation(true, WriterState.Running), new JournalWriterStore(database).Observe(id));
        var plan = new PreparationPlanCodec(instance, new(), database).Read(report.GetProperty("plan").Deserialize<BlobReference>()!).ReadWire();
        var path = Path.Combine(instance.RuntimeRoot, plan.Candidate.RelativePath);
        var before = File.Exists(path) ? PhysicalObjectIdentity.Read(path) : null;
        if (point != SecretPreparationPoint.Published)
        {
            var frozenDatabase = Fingerprints(database.DatabasePath);
            var rejection = Record.Exception(() => StateDatabase.OpenExisting(instance, Path.Combine(root, "live-writer-refused"), new JournalStartupAdmission(instance, new())));
            Assert.NotNull(rejection);
            Assert.True(rejection is IOException or InvalidOperationException);
            Assert.Equal(frozenDatabase, Fingerprints(database.DatabasePath));
        }
        var stopped = await owner.StopAsync(); Assert.True(stopped.CompleteJobStopped);
        var exit = await owner.AwaitExitAsync(); Assert.NotEqual(0, exit);
        Assert.Equal(new WriterStopObservation(true, WriterState.Stopped), new JournalWriterStore(database).Observe(id));
        using var lease = InstanceLease.Acquire(instance, new());
        var roots = new PayloadApplyRoots(Path.Combine(root, "host"), Path.Combine(instance.RuntimeRoot, "extensions"));
        var reopened = StateDatabase.OpenExisting(instance, Path.Combine(root, "startup-inspection"), new JournalStartupAdmission(instance, new(), roots));
        var index = new StartupJournalRecovery(instance, new(), reopened, roots).Recover(lease);
        var final = Assert.Single(new RecoveryJournalRegistry(reopened).ReadSnapshot().Journals);
        Assert.Equal(phase, final.Phase);
        var store = new SecretFieldStore(instance, reopened);
        var state = await store.GetStateAsync(key, default); Assert.Equal(published, state.IsConfigured);
        if (published)
        {
            using var value = await store.ReadAsync(key, default);
            value.Use(chars => Assert.True(chars.SequenceEqual("secret-crash-owned-fixture")));
        }
        Assert.Equal(before, File.Exists(path) ? PhysicalObjectIdentity.Read(path) : null);
        Assert.True(index.Current.IsReady); Assert.Equal(phase == JournalPhase.Quarantined, index.Current.Isolations.Count != 0);
        var second = new StartupJournalRecovery(instance, new(), reopened, roots).Recover(lease);
        Assert.True(second.Current.IsReady);
        using var supplied = new SecretValue("secret-crash-owned-fixture");
        if (published)
            Assert.Equal(state, await store.ApplyAsync(Guid.Parse(plan.OperationId), key, 0, SecretDisposition.Set, supplied, default));
        else
        {
            var rejection = await Assert.ThrowsAsync<IOException>(async () => await store.ApplyAsync(Guid.Parse(plan.OperationId), key, 0, SecretDisposition.Set, supplied, default));
            Assert.Equal(phase == JournalPhase.Committed ? "secret_preparation_not_executed" : "secret_preparation_recovery_pending", rejection.Message);
        }
        using var changed = new SecretValue("different-secret-crash-fixture");
        await Assert.ThrowsAsync<NexusPipeline.Core.Operations.OperationConflictException>(async () =>
            await store.ApplyAsync(Guid.Parse(plan.OperationId), key, 0, SecretDisposition.Set, changed, default));
        Assert.Equal(before, File.Exists(path) ? PhysicalObjectIdentity.Read(path) : null);
        output.WriteLine(JsonSerializer.Serialize(new { point, identity, stopped, exit, report, final, published, preservedIdentity = before }));
    }

    private static string[] Fingerprints(string path) => new[] { "", "-wal", "-shm", "-journal" }.Where(suffix => File.Exists(path + suffix))
        .Select(suffix => suffix + ":" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path + suffix)))).ToArray();
    private static string[] RuntimeFingerprints(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(path) != "instance.lock").Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
}
