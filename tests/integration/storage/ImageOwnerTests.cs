using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Records;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ImageOwnerTests
{
    [Theory(DisplayName = "An Actual Captured Image Retirement Owner Exit Preserves Its Png And Exact Producer While Recovering Prepared Cleanup")]
    [InlineData("ImageRetirementAccepted", false)]
    [InlineData("ImageRetirementMaterialized", true)]
    public async Task PreservesRetiredProducer(
        string boundary, bool materialized)
    {
        using var fixture = new PcCrashFixture(nativeWindow: true, retirementCapture: true);
        await fixture.StartAsync(boundary);
        var source = fixture.Source;
        var receipt = Assert.IsType<AttemptReceipt>(new AttemptReceiptStore(source.Database).Read(fixture.Attempt));
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
        var journal = new JournalStore(source.Instance, new()).Read(fixture.Parent).Export();
        var images = new ImageCatalogReader(source.Instance, source.Database, new());
        var image = Assert.Single(images.ReadPage(new(receipt.Facts.RunId, fixture.Attempt)).Items);
        Assert.Equal(RunImageOutcome.Captured, image.Outcome);
        Assert.Equal(RecordContentAvailability.Retired, image.ContentAvailability);
        Assert.Throws<RetiredContentException>(() => images.OpenOriginal(image.Key, image.ResultSha256));
        var registered = Assert.Single(new RecoveryJournalRegistry(source.Database).ReadSnapshot().Journals,
            item => item.Kind == JournalKind.FileCleanup);
        Assert.Equal(JournalPhase.Prepared, registered.Phase); Assert.Equal(1, registered.Revision);
        Assert.Equal(materialized, Directory.Exists(Path.Combine(source.Instance.RuntimeRoot, "journals", registered.JournalId.ToString("D"))));
        BlobReference proof;
        using (var connection = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true))
            proof = Assert.IsType<BlobReference>(RecordRetirementRows.Find(connection, null, registered.OperationId));
        var blobs = new ImmutableBlobStore(source.Instance, new());
        var document = RecordRetirementCodec.Read<RecordRetirementDocument>(blobs, proof);
        Assert.Equal(RetirementContentKind.Images, document.ContentKind);
        var content = Assert.Single(document.Contents); Assert.Equal(image.ImageSha256, content.Sha256);
        byte[] png;
        using (var input = blobs.OpenVerified(content))
        { using var bytes = new MemoryStream(); input.CopyTo(bytes); png = bytes.ToArray(); }
        Assert.NotEmpty(png);
        await fixture.KillOwnerAsync();
        using var lease = InstanceLease.Acquire(source.Instance, new());
        var roots = new PayloadApplyRoots(Path.Combine(source.Root, "host"), Path.Combine(source.Instance.RuntimeRoot, "extensions"));
        var database = StateDatabase.OpenExisting(source.Instance, Path.Combine(source.Root, "image-retirement-restart"),
            new JournalStartupAdmission(source.Instance, new(), roots));
        var recovery = new StartupJournalRecovery(source.Instance, new(), database, roots);
        var index = recovery.Recover(lease); Assert.True(index.Current.IsReady);
        var snapshot = new RecoveryJournalRegistry(database).ReadSnapshot();
        Assert.Equal(JournalPhase.Committed, Assert.Single(snapshot.Journals,
            item => item.JournalId == registered.JournalId).Phase);
        var effects = snapshot.FileEffects.Where(item => item.JournalId == registered.JournalId).ToArray();
        Assert.NotEmpty(effects); Assert.All(effects, item => Assert.Equal(FileEffectState.NotExecuted, item.State));
        var after = new ImageCatalogReader(source.Instance, database, new());
        Assert.Equal(image, Assert.Single(after.ReadPage(new(receipt.Facts.RunId, fixture.Attempt)).Items));
        Assert.Throws<RetiredContentException>(() => after.OpenOriginal(image.Key, image.ResultSha256));
        RequireOriginal();
        var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
        var service = new ContentRetirementService(source.Instance, database, new(), lease, resources, index);
        var replay = Assert.IsType<ContentRetirementPage>(service.RetireImagesPage(
            registered.OperationId, document.SubjectId, receipt.Facts.RunId, document.AfterSha256, document.Limit));
        Assert.Equal(registered.JournalId, replay.CleanupJournalId); Assert.Equal(1, replay.Count);
        _ = recovery.Recover(lease); RequireOriginal();
        void RequireOriginal()
        {
            Assert.Equal(journal, new JournalStore(source.Instance, new()).Read(fixture.Parent).Export());
            Assert.Equal(receipt, new AttemptReceiptStore(database).Read(fixture.Attempt));
            Assert.Equal(fixture.OriginalBytes, File.ReadAllBytes(source.Configuration));
            Assert.Equal(fixture.OriginalIdentity, PhysicalObjectIdentity.Read(source.Configuration));
            using var input = blobs.OpenVerified(content); using var bytes = new MemoryStream(); input.CopyTo(bytes);
            Assert.Equal(png, bytes.ToArray());
            Assert.Contains(new RecordAssetStore(database, blobs).ReadForRun(receipt.Facts.RunId).Items,
                asset => asset.Kind == RecordAssetKind.Screenshot && asset.Sha256 == image.ImageSha256);
        }
    }
}
