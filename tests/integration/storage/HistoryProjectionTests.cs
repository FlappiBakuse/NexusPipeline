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
    [Theory(DisplayName = "Cold Admission Verifies Complete Execution Catalogs And Rejects Changed Log Image Or Closure Bindings")]
    [InlineData("valid")]
    [InlineData("log")]
    [InlineData("image")]
    [InlineData("closure")]
    public async Task AdmitsVerifiedHistory(string fault)
    {
        var fixture = new PcStageFixture(hooks: false, nativeWindow: true);
        await fixture.ExecuteAsync(async () =>
        {
            _ = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            var source = fixture.Source;
            var projection = new HistoryProjection(source.Instance, source.Database, new(), source.Lease);
            await projection.RunAsync(default); Assert.True(projection.IsComplete);
            var changed = fault switch
            {
                "log" => "UPDATE records.execution_log_entries SET recorded_bytes=recorded_bytes+1,observed_bytes=observed_bytes+1;",
                "image" => "UPDATE records.execution_image_entries SET png_size=png_size+1 WHERE png_size IS NOT NULL;",
                "closure" => "UPDATE main.journal_writer_job_closures SET plan_size=plan_size+1;",
                _ => ""
            };
            if (changed.Length > 0) fixture.Base.Execute(changed);
            try
            {
                var sequence = source.Database.CommitSequence;
                void Read() => StateDatabase.OpenExisting(source.Instance, Path.Combine(source.Root, "admission-diagnostics"),
                    new NexusPipeline.Platform.Storage.Journals.JournalStartupAdmission(source.Instance, new()));
                if (fault == "valid") Read();
                else Assert.ThrowsAny<IOException>(Read);
                Assert.Equal(sequence, source.Database.CommitSequence);
            }
            finally
            {
                if (changed.Length > 0) fixture.Base.Execute(changed.Replace("+1", "-1", StringComparison.Ordinal));
            }
        });
    }

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
        if (fail) fixture.Base.Execute("CREATE TRIGGER records.reject_history BEFORE INSERT ON execution_image_catalog BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        using var stopping = new CancellationTokenSource();
        if (!fail) stopping.Cancel();
        await first.RunAsync(stopping.Token);
        Assert.False(first.IsComplete); Assert.Equal(fail ? "failed" : "pending", first.Status);
        if (fail) fixture.Base.Execute("DROP TRIGGER records.reject_history;");
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
            fixture.Base.Execute("DELETE FROM records.execution_image_entries; DELETE FROM records.execution_image_catalog;");
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
