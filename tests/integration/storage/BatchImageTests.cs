using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Media;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Records;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class BatchImageTests(ITestOutputHelper output)
{
    [Fact(DisplayName = "Actual Batch Image Retirement Skips Previously Retired Content And Preserves Sorted Page Membership")]
    public async Task RetiresSortedImagePages()
    {
        var fixture = new PcStageFixture(launch: false, external: true, hooks: false,
            nativeWindow: true, captureFrames: 40);
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            var source = fixture.Source;
            new ExecutionRecordProjection(source.Instance, source.Database, new(), source.Lease,
                new(source.Instance, source.Database, new())).OnCommitted(receipt);
            var run = receipt.Facts.RunId;
            var images = new ImageCatalogReader(source.Instance, source.Database, new());
            var query = new ImageCatalogQuery(run, receipt.Facts.AttemptId);
            var firstView = images.ReadPage(query).Items;
            Assert.Equal(40, firstView.Count);
            var ordered = firstView.OrderBy(item => item.ImageSha256, StringComparer.Ordinal).ToArray();
            Assert.Equal(ordered.Length, ordered.Select(item => item.ImageSha256).Distinct().Count());
            var service = new ContentRetirementService(source.Instance, source.Database, new(),
                source.Lease, fixture.Base.Resources, fixture.Base.Index, source.PayloadRoots);

            var first = Assert.IsType<ContentRetirementPage>(service.RetireImagesPage(Guid.NewGuid(), "image-retirement", run, limit: 12));
            Assert.Equal(12, first.Count);
            Assert.Equal(ordered[11].ImageSha256, first.LastSha256);
            Assert.True(service.CompleteCleanup(first));

            var second = Assert.IsType<ContentRetirementPage>(service.RetireImagesPage(Guid.NewGuid(), "image-retirement", run, limit: 5));
            Assert.Equal(5, second.Count);
            Assert.Equal(ordered[16].ImageSha256, second.LastSha256);
            Assert.True(service.CompleteCleanup(second));

            RecordRetirementInventory.VerifyReadOnly(source.Instance, source.Database, new());
            var lastProof = new[] { first.OperationId.ToString("D"), second.OperationId.ToString("D") }.Max(StringComparer.Ordinal);
            var proof = $" WHERE operation_id='{lastProof}';";
            fixture.Base.Execute("UPDATE records.record_content_retirements SET size_bytes=size_bytes+1" + proof);
            try { Assert.ThrowsAny<IOException>(() => RecordRetirementInventory.VerifyReadOnly(source.Instance, source.Database, new())); }
            finally { fixture.Base.Execute("UPDATE records.record_content_retirements SET size_bytes=size_bytes-1" + proof); }

            var after = images.ReadPage(query).Items;
            Assert.Equal(firstView.Select(item => item.Key), after.Select(item => item.Key));
            Assert.Equal(ordered.Take(17).Select(item => item.ImageSha256),
                after.Where(item => item.ContentAvailability == RecordContentAvailability.Retired)
                    .Select(item => item.ImageSha256).Order(StringComparer.Ordinal));
            Assert.Throws<RetiredContentException>(() => images.OpenOriginal(ordered[7].Key, ordered[7].ResultSha256));
            using var original = images.OpenOriginal(ordered[17].Key, ordered[17].ResultSha256);
            Assert.Equal(ordered[17].ImageSha256, Convert.ToHexStringLower(SHA256.HashData(original)));
        });
    }

    [Fact(DisplayName = "An Actual Batch Image Catalog Preserves Ordered Pages And Revalidates Off-Page Rows")]
    public async Task PagesVerifiedBatchFrames()
    {
        var fixture = new PcStageFixture(launch: false, external: true, hooks: false,
            nativeWindow: true, captureFrames: 12);
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            var source = fixture.Source;
            var validated = AttemptPreparedBinding.Read(source.Instance, new(), source.Database, receipt.JournalId).ValidatedRead;
            var expected = Assert.Single(validated.Captures).Result!.Frames!;
            Assert.Equal(12, expected.Count);
            new ImageCatalogProjection(source.Instance, source.Database, new(), source.Lease).OnCommitted(receipt);
            var reader = new ImageCatalogReader(source.Instance, source.Database, new());
            var query = new ImageCatalogQuery(receipt.Facts.RunId, receipt.Facts.AttemptId, Limit: 5);
            var first = reader.ReadPage(query);
            Assert.Equal(4, first.NextPosition);
            var next = reader.ReadPage(query with { AfterPosition = first.NextPosition!.Value });
            Assert.Equal(9, next.NextPosition);
            var end = reader.ReadPage(query with { AfterPosition = next.NextPosition!.Value });
            Assert.Null(end.NextPosition);
            var past = reader.ReadPage(query with { AfterPosition = 11 });
            Assert.Empty(past.Items);
            Assert.Null(past.NextPosition);

            var items = first.Items.Concat(next.Items).Concat(end.Items).ToArray();
            Assert.Equal(Enumerable.Range(0, 12), items.Select(item => item.Position));
            Assert.Equal(expected.Select(frame => frame.FrameId), items.Select(item => item.Key.CaptureId));
            Assert.Equal(expected.Select(frame => frame.Png.Sha256), items.Select(item => item.ImageSha256));
            Assert.All(items, item =>
            {
                Assert.Equal(RunImageOutcome.Captured, item.Outcome);
                Assert.Equal(1, item.InvocationOrdinal);
            });
            using (var original = reader.OpenOriginal(end.Items[^1].Key, end.Items[^1].ResultSha256))
                Assert.Equal(expected[^1].Png.Sha256, Convert.ToHexStringLower(SHA256.HashData(original)));

            var row = $" WHERE attempt_id='{receipt.Facts.AttemptId:D}' AND position=11;";
            fixture.Base.Execute("UPDATE records.execution_image_entries SET width=width-1" + row);
            try { Assert.ThrowsAny<IOException>(() => reader.ReadPage(query)); }
            finally { fixture.Base.Execute("UPDATE records.execution_image_entries SET width=width+1" + row); }
            Assert.Equal(first.Items, reader.ReadPage(query).Items);
        });
    }

    [Fact(DisplayName = "Two Frame Identities Sharing One PNG Retire Together As One Content Blob")]
    public async Task RetiresSharedContent()
    {
        var fixture = new PcStageFixture(launch: false, external: true, hooks: false,
            nativeWindow: true, captureFrames: 2, distinctCaptureFrames: false);
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            var source = fixture.Source;
            new ExecutionRecordProjection(source.Instance, source.Database, new(), source.Lease,
                new(source.Instance, source.Database, new())).OnCommitted(receipt);
            var reader = new ImageCatalogReader(source.Instance, source.Database, new());
            var query = new ImageCatalogQuery(receipt.Facts.RunId, receipt.Facts.AttemptId);
            var before = reader.ReadPage(query).Items;
            Assert.Equal(2, before.Count);
            Assert.Equal(2, before.Select(item => item.Key.CaptureId).Distinct().Count());
            Assert.Single(before.Select(item => item.ImageSha256).Distinct());
            using (var first = reader.OpenOriginal(before[0].Key, before[0].ResultSha256))
            using (var second = reader.OpenOriginal(before[1].Key, before[1].ResultSha256))
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(first)),
                    Convert.ToHexStringLower(SHA256.HashData(second)));
            var retirement = new ContentRetirementService(source.Instance, source.Database, new(),
                source.Lease, fixture.Base.Resources, fixture.Base.Index, source.PayloadRoots);
            Assert.Equal(1, retirement.RetireImagesPage(Guid.NewGuid(), "image-retirement",
                receipt.Facts.RunId, limit: 1)!.Count);
            var after = reader.ReadPage(query).Items;
            Assert.Equal(before.Select(item => item.Key), after.Select(item => item.Key));
            Assert.All(after, item => Assert.Equal(RecordContentAvailability.Retired, item.ContentAvailability));
            foreach (var item in after)
                Assert.Throws<RetiredContentException>(() => reader.OpenOriginal(item.Key, item.ResultSha256));
        });
    }

    [Fact(DisplayName = "Two Actual Batch Frames Retire Independently While Their Shared Capture Evidence Stays Bound")]
    public async Task RetiresSelectedFrame()
    {
        var fixture = new PcStageFixture(launch: false, external: true, hooks: false,
            nativeWindow: true, captureFrames: 2);
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            var source = fixture.Source;
            new ExecutionRecordProjection(source.Instance, source.Database, new(), source.Lease,
                new(source.Instance, source.Database, new())).OnCommitted(receipt);
            var reader = new ImageCatalogReader(source.Instance, source.Database, new());
            var query = new ImageCatalogQuery(receipt.Facts.RunId, receipt.Facts.AttemptId);
            var before = reader.ReadPage(query).Items;
            Assert.Equal(2, before.Count);
            Assert.Equal(2, before.Select(item => item.ImageSha256).Distinct().Count());
            var validated = AttemptPreparedBinding.Read(source.Instance, new(), source.Database, receipt.JournalId).ValidatedRead;
            var retirement = new ContentRetirementService(source.Instance, source.Database, new(),
                source.Lease, fixture.Base.Resources, fixture.Base.Index, source.PayloadRoots);
            _ = Assert.IsType<ContentRetirementPage>(retirement.RetireImagesPage(Guid.NewGuid(),
                "image-retirement", receipt.Facts.RunId, limit: 1));
            var after = reader.ReadPage(query).Items;
            var retired = Assert.Single(after, item => item.ContentAvailability == RecordContentAvailability.Retired);
            var retained = Assert.Single(after, item => item.ContentAvailability != RecordContentAvailability.Retired);
            var capture = Assert.Single(validated.Captures);
            Assert.Throws<RetiredContentException>(() => new CaptureEvidenceReader(source.Instance, source.Database, new())
                .OpenOriginalCaptured(capture.State.Binding, capture.State.Result!, retired.Key.CaptureId, validated));
            Assert.Throws<RetiredContentException>(() => reader.OpenOriginal(retired.Key, retired.ResultSha256));
            using var original = reader.OpenOriginal(retained.Key, retained.ResultSha256);
            Assert.Equal(retained.ImageSha256, Convert.ToHexStringLower(SHA256.HashData(original)));
            Assert.Equal(before.Select(item => item.Key), after.Select(item => item.Key));
        });
    }

    [Fact(DisplayName = "One Actual PC Step Captures And Catalogs Two Hundred Distinct Window Frames In One Attempt")]
    public async Task CapturesDistinctFrames()
    {
        var fixture = new PcStageFixture(launch: false, external: true, hooks: false, accept: false,
            nativeWindow: true, captureFrames: 200);
        await fixture.ExecuteAsync(async () =>
        {
            fixture.Accept();
            var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
            Assert.Equal(EngineOutcome.Succeeded, receipt.Facts.Engine);
            var state = fixture.State;
            Assert.Single(state.Total.Steps);
            Assert.Equal(200, state.Total.CaptureRuntime!.MaximumFramesPerStep);
            var history = new JournalStore(fixture.Source.Instance, new()).ReadAllRevisions(state.Journal.JournalId);
            Assert.InRange(history.Sum(item => item.Export().LongLength), 1, JournalCapacityLimits.MaximumHistoryBytes);
            var saved = Assert.Single(new CaptureEvidenceReader(fixture.Source.Instance, fixture.Source.Database, new())
                .ReadMetadata(state.Journal, state.Attempt, state.Total, history));
            Assert.Equal("captured", saved.Result!.Outcome);
            Assert.Null(saved.Result.Png);
            Assert.Equal(200, saved.Result.Frames!.Count);
            Assert.Equal(saved.State.Binding.CaptureId, saved.Result.Frames[0].FrameId);
            Assert.True(fixture.GameIsAlive);

            var images = new ImageCatalogReader(fixture.Source.Instance, fixture.Source.Database, new());
            var projection = new ImageCatalogProjection(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Source.Lease);
            projection.OnCommitted(receipt);
            var items = new List<ImageCatalogItem>();
            int? after = null;
            do
            {
                var page = images.ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId, after ?? -1, 100));
                items.AddRange(page.Items); after = page.NextPosition;
            } while (after is not null);
            Assert.Equal(200, items.Count);
            Assert.All(items, item => { Assert.True(item.Width >= 640); Assert.True(item.Height >= 480); });
            Assert.Equal(Enumerable.Range(0, 200), items.Select(item => item.Position));
            Assert.Equal(200, items.Select(item => item.Key.CaptureId).Distinct().Count());
            Assert.Equal(200, items.Select(item => item.ImageSha256).Distinct().Count());
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                Assert.Equal(RunImageOutcome.Captured, item.Outcome);
                using var original = images.OpenOriginal(item.Key, item.ResultSha256);
                RgbCapturePng.Verify(original, item.Width!.Value, item.Height!.Value);
                original.Position = 0;
                var digest = Convert.ToHexStringLower(SHA256.HashData(original));
                Assert.Equal(item.ImageSha256, digest);
                Assert.True(hashes.Add(digest));
            }
            new ExecutionRecordProjection(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Source.Lease,
                new(fixture.Source.Instance, fixture.Source.Database, new())).OnCommitted(receipt);
            Assert.Equal(200, fixture.Base.Count("record_assets"));
            using (var original = images.OpenOriginal(items[0].Key, items[0].ResultSha256))
            using (var incomplete = new MemoryStream())
            {
                var bytes = new byte[checked((int)original.Length)]; original.ReadExactly(bytes);
                var request = Guid.NewGuid(); var header = new byte[40];
                "NXSCAP02"u8.CopyTo(header); request.TryWriteBytes(header.AsSpan(8, 16));
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), items[0].Width!.Value);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(32), items[0].Height!.Value);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(36), bytes.Length);
                incomplete.Write(header); incomplete.Write(bytes); incomplete.Position = 0;
                using var batch = new CaptureBatchSpool(fixture.Source.Instance, request,
                    items[0].Width!.Value, items[0].Height!.Value, 2);
                await Assert.ThrowsAnyAsync<IOException>(() => batch.ReadOutputAsync(incomplete, CancellationToken.None));
                Assert.Throws<IOException>(() => batch.Frames());
                Assert.Equal(100, images.ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId, 99, 100)).Items.Count);
            }
            output.WriteLine($"P2_BATCH_IMAGE_DATASET runtime={fixture.Source.Instance.RuntimeRoot} " +
                $"runId={receipt.Facts.RunId:D} attemptId={receipt.Facts.AttemptId:D} images={items.Count} " +
                $"journalRevisions={history.Count} journalBytes={history.Sum(item => item.Export().LongLength)}");
        });
    }
}
