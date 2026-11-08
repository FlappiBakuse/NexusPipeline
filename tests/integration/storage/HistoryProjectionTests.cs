using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class HistoryProjectionTests
{
    [Theory(DisplayName = "Stopped Or Failed Historical Backfill Resumes Without Reexecuting An Attempt")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumesMissingCatalogs(bool fail)
    {
        using var fixture = new ExecutionHooksFixture(false);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        var source = fixture.Source;
        var first = new HistoryProjection(source.Instance, source.Database, new(), source.Lease);
        Assert.False(first.IsComplete);
        Assert.False(first.IsAvailable(receipt.Facts.RunId, receipt.Facts.AttemptId, false));
        if (fail) fixture.Base.Execute("CREATE TRIGGER reject_history BEFORE INSERT ON execution_image_catalog BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        using var stopping = new CancellationTokenSource();
        if (!fail) stopping.Cancel();
        await first.RunAsync(stopping.Token);
        Assert.False(first.IsComplete); Assert.Equal(fail ? "failed" : "pending", first.Status);
        if (fail) fixture.Base.Execute("DROP TRIGGER reject_history;");
        var next = new HistoryProjection(source.Instance, source.Database, new(), source.Lease);
        await next.RunAsync(default);
        Assert.True(next.IsComplete);
        var rows = new LogCatalogReader(source.Instance, source.Database, new()).ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId));
        Assert.Equal(3, rows.Items.Count);
        Assert.True(next.IsAvailable(receipt.Facts.RunId, receipt.Facts.AttemptId, false));
        Assert.True(next.IsAvailable(receipt.Facts.RunId, receipt.Facts.AttemptId, true));
        var restarted = new HistoryProjection(source.Instance, source.Database, new(), source.Lease);
        await restarted.RunAsync(default);
        Assert.Equal(rows.Items.ToArray(), new LogCatalogReader(source.Instance, source.Database, new())
            .ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId)).Items.ToArray());
        Assert.Equal(receipt, new AttemptReceiptStore(source.Database).Read(receipt.Facts.AttemptId));
        Assert.Equal(0, fixture.Base.Inbox.PendingCount);
    }

    [Fact(DisplayName = "A Captured Image Remains Referenced While Its Historical Index Is Missing")]
    public async Task ProtectsUnindexedImage()
    {
        var fixture = new PcStageFixture(hooks: false, nativeWindow: true);
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            var source = fixture.Source;
            new ImageCatalogProjection(source.Instance, source.Database, new(), source.Lease).OnCommitted(receipt);
            var image = Assert.Single(new ImageCatalogReader(source.Instance, source.Database, new()).ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId)).Items);
            fixture.Base.Execute("DELETE FROM execution_image_entries; DELETE FROM execution_image_catalog;");
            var blob = new BlobReference($"blobs/content/{image.ImageSha256}.bin", image.SizeBytes!.Value, image.ImageSha256!);
            var references = new BlobReferenceIndex(source.Instance, new(), source.Database, new(source.Instance, new()));
            Assert.True(references.IsReferenced(blob));
            var projection = new HistoryProjection(source.Instance, source.Database, new(), source.Lease);
            await projection.RunAsync(default); Assert.True(projection.IsComplete);
            using var original = new ImageCatalogReader(source.Instance, source.Database, new()).OpenOriginal(image.Key, image.ResultSha256);
            Assert.Equal(blob.SizeBytes, original.Length);
        });
    }
}
