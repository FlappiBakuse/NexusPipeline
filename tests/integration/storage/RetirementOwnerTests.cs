using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Records;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RetirementOwnerTests
{
    [Theory(DisplayName = "An Actual Owner Killed After Retirement Commit Recovers The Existing Prepared Cleanup Without Claiming Deletion")]
    [InlineData("retirementAccepted", false)]
    [InlineData("retirementMaterialized", true)]
    public async Task RecoversPreparedCleanup(
        string boundary, bool materialized)
    {
        await using var fixture = await ProviderCrashFixture.StartAsync(
            collected: true, committed: true, boundary: boundary);
        var source = fixture.Source;
        var receipt = Assert.IsType<AttemptReceipt>(new AttemptReceiptStore(source.Database).Read(fixture.Attempt));
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
        var original = new JournalStore(source.Instance, new()).Read(fixture.Parent).Export();
        var before = new LogCatalogReader(source.Instance, source.Database, new())
            .ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId)).Items.ToArray();
        var registered = Assert.Single(new RecoveryJournalRegistry(source.Database).ReadSnapshot().Journals,
            item => item.Kind == JournalKind.FileCleanup);
        Assert.Equal(JournalPhase.Prepared, registered.Phase);
        Assert.Equal(materialized, Directory.Exists(Path.Combine(source.Instance.RuntimeRoot, "journals", registered.JournalId.ToString("D"))));
        BlobReference proof;
        using (var connection = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true))
            proof = Assert.IsType<BlobReference>(RecordRetirementRows.Find(connection, null, registered.OperationId));
        var blobs = new ImmutableBlobStore(source.Instance, new());
        var document = RecordRetirementCodec.Read<RecordRetirementDocument>(blobs, proof);
        Assert.NotEmpty(document.Contents);
        var pending = new FileEffectOutbox(source.Database).Read(registered.JournalId);
        Assert.NotEmpty(pending);

        await fixture.KillOwnerAsync();
        Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(fixture.Root));
        using var lease = InstanceLease.Acquire(source.Instance, new());
        var roots = new PayloadApplyRoots(Path.Combine(source.Root, "host"), Path.Combine(source.Instance.RuntimeRoot, "extensions"));
        var database = StateDatabase.OpenExisting(source.Instance, Path.Combine(source.Root, "retirement-restart"),
            new JournalStartupAdmission(source.Instance, new(), roots));
        var recovery = new StartupJournalRecovery(source.Instance, new(), database, roots);
        var index = recovery.Recover(lease);
        Assert.True(index.Current.IsReady);
        var completed = new RecoveryJournalRegistry(database).ReadJournal(registered.JournalId);
        Assert.Equal(JournalPhase.Committed, completed.Phase);
        var outbox = new FileEffectOutbox(database);
        var effects = outbox.Read(registered.JournalId);
        Assert.Equal(pending.Select(item => (item.StepId, item.IntentDigest, State: FileEffectState.NotExecuted)),
            effects.Select(item => (item.StepId, item.IntentDigest, item.State)));
        foreach (var content in document.Contents) blobs.Verify(content);
        var after = new LogCatalogReader(source.Instance, database, new())
            .ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId)).Items.ToArray();
        Assert.Equal(before, after);
        Assert.Contains(after, item => item.ContentAvailability == RecordContentAvailability.Retired);
        Assert.Equal(original, new JournalStore(source.Instance, new()).Read(fixture.Parent).Export());
        Assert.Equal(receipt, new AttemptReceiptStore(database).Read(fixture.Attempt));
        Assert.Equal(fixture.OriginalBytes, File.ReadAllBytes(source.Configuration));
        Assert.Equal(fixture.OriginalIdentity, PhysicalObjectIdentity.Read(source.Configuration));
        var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
        var service = new ContentRetirementService(source.Instance, database, new(), lease, resources, index);
        var replay = Assert.IsType<ContentRetirementPage>(service.RetireLogsPage(
            registered.OperationId, document.SubjectId, receipt.Facts.RunId, document.AfterSha256, document.Limit));
        Assert.Equal(registered.JournalId, replay.CleanupJournalId);
        Assert.Equal(document.Contents.Length, replay.Count);
        _ = recovery.Recover(lease);
        Assert.Equal(effects, outbox.Read(registered.JournalId));
    }
}
