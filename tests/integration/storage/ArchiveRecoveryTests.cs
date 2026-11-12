using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ArchiveRecoveryTests
{
    [Theory(DisplayName = "Killed Archive Sweep Owners Reuse Or Continue The Durable Cleanup Receipt")]
    [InlineData("accepted", JournalPhase.Committed, FileEffectState.NotExecuted, false)]
    [InlineData("started", JournalPhase.Committed, FileEffectState.Completed, false)]
    [InlineData("removed", JournalPhase.Quarantined, FileEffectState.Started, false)]
    [InlineData("accepted", JournalPhase.Committed, FileEffectState.NotExecuted, true)]
    [InlineData("started", JournalPhase.Committed, FileEffectState.Completed, true)]
    [InlineData("removed", JournalPhase.Quarantined, FileEffectState.Started, true)]
    public async Task ContinuesFromReceipts(string point, JournalPhase phase, FileEffectState effect, bool content)
    {
        using var package = new BoundPackageFixture("data-specialized");
        package.Publish();
        var instance = package.Catalog.Instance;
        var roots = new PayloadApplyRoots(Path.Combine(package.Root, "host"), Path.Combine(instance.RuntimeRoot, "extensions"));
        Directory.CreateDirectory(roots.ExtensionPayloadRoot);
        var input = Path.Combine(package.Root, "archive-input.json");
        var report = Path.Combine(package.Root, "archive-report.json");
        var remove = Guid.NewGuid();
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new
        {
            Instance = instance, package.Archive, PublicKey = package.Catalog.Signer.ExportSubjectPublicKeyInfo(),
            InitialOperationId = package.OperationId, RemoveOperationId = remove, Roots = roots,
            Point = (content ? "content-" : "archive-") + point, Report = report
        }));
        package.Catalog.Lease.Dispose();
        using var process = new OwnedCrashProcess();
        var owner = process.Start(["extension-work-recovery", input], package.Root, instance.InstanceId,
            remove, Path.Combine(package.Root, "native-owner.json"));
        var reached = await process.ReadReportAsync(report, TimeSpan.FromSeconds(90));
        Assert.Equal(owner.ProcessId, reached.GetProperty("processId").GetInt32());
        Assert.True((await process.StopAsync()).CompleteJobStopped);
        _ = await process.AwaitExitAsync();

        var journalId = reached.GetProperty("journalId").GetGuid();
        var producer = reached.GetProperty("ProducerJournalId").GetGuid();
        var archive = reached.GetProperty("Archive").Deserialize<BlobReference>()!;
        var path = Path.Combine(instance.RuntimeRoot, archive.RelativePath);
        var contracts = package.Catalog.Contracts;
        using var lease = InstanceLease.Acquire(instance, contracts);
        var database = StateDatabase.OpenExisting(instance, Path.Combine(package.Root, "inspection"),
            new JournalStartupAdmission(instance, contracts, roots));
        using var reads = BlobReadScope.Hold(instance);
        var recovery = new StartupJournalRecovery(instance, contracts, database, roots).Recover(lease);
        var registry = new RecoveryJournalRegistry(database);
        var registered = registry.ReadJournal(journalId);
        var outbox = new FileEffectOutbox(database);
        Assert.Equal(phase, registered.Phase);
        Assert.Equal(effect, Assert.Single(outbox.Read(journalId)).State);
        Assert.Equal(point == "accepted", File.Exists(path));
        var store = new JournalStore(instance, contracts);
        var original = store.Read(journalId).Export();
        var resources = new ResourceLeaseManager(new HostLifecycle(recovery, TimeProvider.System));
        var cleanup = new ExtensionArchiveCleanup(instance, database, lease, contracts, roots, resources, recovery);
        var result = cleanup.RunNextPage(producer, archive, default)!;
        Assert.Equal(point == "accepted", result.Acceptance.IsNew);
        Assert.Equal(point != "accepted", result.JournalId == journalId);
        Assert.Equal(phase == JournalPhase.Quarantined ? "accepted" : "completed", result.Acceptance.Status);
        Assert.False(File.Exists(path));
        var replay = cleanup.RunNextPage(producer, archive, default)!;
        Assert.False(replay.Acceptance.IsNew);
        Assert.Equal(result.JournalId, replay.JournalId);
        Assert.Equal(result.Plan, replay.Plan);
        Assert.Equal(original, store.Read(journalId).Export());
        Assert.Equal(reached.GetProperty("ProducerBytes").GetBytesFromBase64(), store.Read(producer).Export());
    }
}
