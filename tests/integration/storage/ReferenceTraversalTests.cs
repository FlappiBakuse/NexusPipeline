using System.Runtime.Versioning;
using System.Globalization;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ReferenceTraversalTests
{
    [Fact(DisplayName = "Captured Image References Keep Their Temporal Prefix Proof And Prevent Physical Removal")]
    public async Task RetainsCapturedImages()
    {
        var fixture = new PcStageFixture(launch: false, external: true, hooks: false, nativeWindow: true);
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            var source = fixture.Source;
            new ImageCatalogProjection(source.Instance, source.Database, new(), source.Lease).OnCommitted(receipt);
            var image = Assert.Single(new ImageCatalogReader(source.Instance, source.Database, new())
                .ReadPage(new ImageCatalogQuery(receipt.Facts.RunId, receipt.Facts.AttemptId)).Items);
            var blobs = new ImmutableBlobStore(source.Instance, new());
            var digest = Assert.IsType<string>(image.ImageSha256);
            var reference = new BlobReference($"blobs/content/{digest}.bin", Assert.IsType<long>(image.SizeBytes), digest);
            var cleanup = new BlobCleanup(source.Instance, blobs, new(source.Instance, new(), source.Database, blobs));
            var item = Assert.Single(new CleanupPlanCodec(source.Instance, new(), blobs)
                .Capture(Guid.NewGuid(), Guid.NewGuid(), [reference]).Items);
            using (var unavailable = new FileStream(Path.Combine(source.Instance.RuntimeRoot, "journals",
                receipt.JournalId.ToString("D"), "0000000001.json"), FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.Equal(CleanupItemState.Unknown, cleanup.Inspect(item));
            Assert.Equal(CleanupStepResult.SkippedReferenced, cleanup.RemoveVerified(item));
            blobs.Verify(reference);
        });
    }

    [Fact(DisplayName = "Committed Reference Discovery Uses The Receipt Anchor While Historical Reads Keep Full Prefix Validation")]
    public async Task UsesTerminalReceipt()
    {
        using var fixture = new ExecutionRuntimeFixture();
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        var source = fixture.Source;
        var directory = Path.Combine(source.Instance.RuntimeRoot, "journals", receipt.JournalId.ToString("D"));
        var definition = fixture.State.Total.DefinitionBundle;
        using var unavailable = new FileStream(Path.Combine(directory, "0000000001.json"),
            FileMode.Open, FileAccess.Read, FileShare.None);
        var blobs = new ImmutableBlobStore(source.Instance, new());
        var index = new BlobReferenceIndex(source.Instance, new(), source.Database, blobs);
        Assert.True(index.IsReferenced(new(definition.RelativePath, definition.SizeBytes, definition.Sha256)));
        var orphan = blobs.PersistContent(new byte[] { 17, 35, 91, 42 }, default);
        Assert.False(index.IsReferenced(orphan));
        Assert.Throws<IOException>(() => new JournalStore(source.Instance, new()).ReadAllRevisions(receipt.JournalId));
        Assert.Equal(receipt, new AttemptReceiptStore(source.Database).Read(receipt.Facts.AttemptId));
    }

    [Theory(DisplayName = "Reference Discovery Refuses A Changed Terminal Snapshot Or Conflicting Receipt Before Removing Content")]
    [InlineData("terminal")]
    [InlineData("receipt")]
    [InlineData("entry")]
    [InlineData("missing")]
    [InlineData("directory")]
    public async Task RejectsUnboundTerminal(string change)
    {
        using var fixture = new ExecutionRuntimeFixture();
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        var source = fixture.Source;
        var journal = new RecoveryJournalRegistry(source.Database).ReadJournal(receipt.JournalId);
        var directory = Path.Combine(source.Instance.RuntimeRoot, "journals", receipt.JournalId.ToString("D"));
        var final = Path.Combine(directory, journal.Revision.ToString("D10", CultureInfo.InvariantCulture) + ".json");
        if (change == "terminal") File.AppendAllText(final, " ");
        else if (change == "entry") File.WriteAllBytes(Path.Combine(directory, "unexpected.json"), new byte[] { 1 });
        else if (change == "missing") File.Delete(final);
        else if (change == "directory") Directory.Move(directory, directory + "-unavailable");
        else source.Database.Write((connection, transaction) =>
        {
            using var command = DatabaseConnections.Command(connection, transaction,
                "UPDATE attempt_receipts SET source_receipt_digest=$0 WHERE attempt_id=$1;", new string('0', 64), receipt.Facts.AttemptId.ToString("D"));
            return command.ExecuteNonQuery();
        });
        var blobs = new ImmutableBlobStore(source.Instance, new());
        var orphan = blobs.PersistContent(new byte[] { 53, 7, 61, 39 }, default);
        var index = new BlobReferenceIndex(source.Instance, new(), source.Database, blobs);
        var frozen = new CleanupPlanCodec(source.Instance, new(), blobs).Capture(Guid.NewGuid(), Guid.NewGuid(), [orphan]);
        var cleanup = new BlobCleanup(source.Instance, blobs, index);
        Assert.Equal(CleanupItemState.Unknown, cleanup.Inspect(frozen.Items[0]));
        blobs.Verify(orphan);
    }

    [Fact(DisplayName = "Cold Reference Discovery Retains Committed Hook Logs And Definitions Under The Actual Deletion Guard")]
    public async Task RetainsCompletedInputs()
    {
        using var fixture = new ExecutionHooksFixture(false);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        var source = fixture.Source;
        new LogCatalogProjection(source.Instance, source.Database, new(), source.Lease).OnCommitted(receipt);
        var reader = new LogCatalogReader(source.Instance, source.Database, new());
        var page = reader.ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId));
        Assert.Equal(3, page.Items.Count);
        var definition = fixture.State.Total.DefinitionBundle;
        var contents = page.Items.SelectMany(item => new ExecutionLogReader(source.Instance, source.Database, new())
            .Describe(item.Key).Parts.Select(part => new BlobReference($"blobs/content/{part.Sha256}.bin", part.SizeBytes, part.Sha256)))
            .Prepend(new(definition.RelativePath, definition.SizeBytes, definition.Sha256)).Distinct().ToArray();
        var blobs = new ImmutableBlobStore(source.Instance, new());
        var index = new BlobReferenceIndex(source.Instance, new(), source.Database, blobs);
        var cleanup = new BlobCleanup(source.Instance, blobs, index);
        var frozen = new CleanupPlanCodec(source.Instance, new(), blobs).Capture(Guid.NewGuid(), Guid.NewGuid(), contents);
        foreach (var item in frozen.Items)
        {
            Assert.Equal(CleanupStepResult.SkippedReferenced, cleanup.RemoveVerified(item));
            blobs.Verify(item.Blob);
        }
        var orphan = blobs.PersistContent(new byte[] { 29, 17, 83, 43 }, default);
        Assert.False(index.IsReferenced(orphan));
        Assert.Equal(page.Items.ToArray(), reader.ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId)).Items.ToArray());
        Assert.Equal(receipt, new AttemptReceiptStore(source.Database).Read(receipt.Facts.AttemptId));
    }

}
