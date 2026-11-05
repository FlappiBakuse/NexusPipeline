using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Records;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ImageRetirementTests
{
    [Fact(DisplayName = "An Actual Captured Image Retires Through The Original Outbox And Reopens As Retired Without Changing Its Capture Or Asset")]
    public async Task RetiresExactImages()
    {
        await ImageRetirementScenario.RunAsync(fixture =>
        {
            var source = fixture.Source; var image = fixture.Image; var path = fixture.PngPath;
            var original = new JournalStore(source.Instance, new()).Read(fixture.Receipt.JournalId).Export();
            var marker = ReadMarker(source, fixture.Receipt.Facts.RunId);
            var operation = Guid.NewGuid(); var page = fixture.Retire(operation);
            Assert.Equal(page, fixture.Retire(operation));
            Assert.Single(fixture.Plan(page).Items, item => item.Blob.Sha256 == image.ImageSha256);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Equal(image with { ContentAvailability = RecordContentAvailability.Retired }, fixture.Image);
                RecordRetirementInventory.VerifyReadOnly(source.Instance, source.Database, new());
                new ExecutionRecordProjection(source.Instance, source.Database, new(), source.Lease,
                    new(source.Instance, source.Database, new())).OnCommitted(fixture.Receipt);
            }
            Assert.Equal(FileCleanupResult.Cleaned, fixture.Cleanup(page)); Assert.False(File.Exists(path));
            Assert.Equal(image with { ContentAvailability = RecordContentAvailability.Retired }, fixture.Image);
            Assert.Throws<RetiredContentException>(() => fixture.Images.OpenOriginal(image.Key, image.ResultSha256));
            var state = AttemptWriterState.Read(source.Instance, source.Database, new(), fixture.Receipt.JournalId);
            var capture = Assert.Single(ExecutionCaptureCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
            Assert.Throws<RetiredContentException>(() =>
                new CaptureEvidenceReader(source.Instance, source.Database, new()).OpenCaptured(capture.Binding, capture.Result!));
            var asset = new RecordAsset(fixture.Receipt.Facts.RunId, image.ImageSha256!, image.SizeBytes!.Value, RecordAssetKind.Screenshot);
            Assert.Throws<RetiredContentException>(() => new RecordAssetStore(source.Database, new(source.Instance, new())).OpenVerified(asset));
            new ExecutionRecordProjection(source.Instance, source.Database, new(), source.Lease, new(source.Instance, source.Database, new()))
                .OnCommitted(fixture.Receipt);
            fixture.RequireOriginal(original, image);
            Assert.Equal(marker, ReadMarker(source, fixture.Receipt.Facts.RunId));
            Assert.Throws<OperationConflictException>(() => fixture.Service.RetireLogsPage(operation, "image-retirement", fixture.Receipt.Facts.RunId));
            fixture.Fixture.Dispose();
            using var reopened = InstanceLease.Acquire(source.Instance, new());
            var database = StateDatabase.OpenExisting(source.Instance, Path.Combine(source.Root, "image-retirement-reopen"),
                new JournalStartupAdmission(source.Instance, new()));
            var reader = new ImageCatalogReader(source.Instance, database, new());
            Assert.Equal(image with { ContentAvailability = RecordContentAvailability.Retired },
                Assert.Single(reader.ReadPage(new(image.Key.RunId, image.Key.AttemptId)).Items));
            Assert.Throws<RetiredContentException>(() => reader.OpenOriginal(image.Key, image.ResultSha256));
            Assert.Equal(fixture.Receipt, new AttemptReceiptStore(database).Read(fixture.Receipt.Facts.AttemptId));
            Assert.Equal(marker, ReadMarker(source, fixture.Receipt.Facts.RunId));
            return Task.CompletedTask;
        });
    }

    [Fact(DisplayName = "Other Asset Kinds And Another Run Keep The Same Png While The Original Screenshot Relation Remains Retired")]
    public async Task KeepsSharedPng()
    {
        await ImageRetirementScenario.RunAsync(fixture =>
        {
            var source = fixture.Source; var image = fixture.Image; var original = new JournalStore(source.Instance, new()).Read(fixture.Receipt.JournalId).Export();
            var assets = new RecordAssetStore(source.Database, new(source.Instance, new()));
            var attached = new RecordAsset(image.Key.RunId, image.ImageSha256!, image.SizeBytes!.Value, RecordAssetKind.Attachment);
            assets.AttachVerified(attached);
            var foreign = fixture.Receipt.Facts with { RunId = Guid.NewGuid(), AttemptId = Guid.NewGuid() };
            new RunRecordStore(source.Database).AppendCompleted([new(Guid.NewGuid(), null, DateTimeOffset.UtcNow, 1, foreign)]);
            var elsewhere = attached with { RunId = foreign.RunId, Kind = RecordAssetKind.Screenshot };
            assets.AttachVerified(elsewhere);
            var page = fixture.Retire(); Assert.Equal(FileCleanupResult.Cleaned, fixture.Cleanup(page));
            Assert.True(File.Exists(fixture.PngPath)); Assert.Equal(RecordContentAvailability.Retired, fixture.Image.ContentAvailability);
            var content = new BlobReference($"blobs/content/{image.ImageSha256}.bin", image.SizeBytes!.Value, image.ImageSha256!);
            Assert.True(new BlobReferenceIndex(source.Instance, new(), source.Database, new(source.Instance, new())).IsReferenced(content));
            using var first = assets.OpenVerified(attached); Assert.Equal(image.SizeBytes!.Value, first.Length);
            using var second = assets.OpenVerified(elsewhere); Assert.Equal(image.SizeBytes!.Value, second.Length);
            Assert.Throws<RetiredContentException>(() => fixture.Images.OpenOriginal(image.Key, image.ResultSha256));
            fixture.RequireOriginal(original, image); return Task.CompletedTask;
        });
    }

    [Theory(DisplayName = "Actual Notification Pending States Protect An Actual Captured Png Without Depending On An Unreleased Sender Lease")]
    [InlineData("queued")]
    [InlineData("sending")]
    [InlineData("unknown")]
    public async Task ProtectsPendingNotifications(string mode)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sink = new NotificationHttpSink(async (_, cancellation) =>
        {
            arrived.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellation); return new(200, "");
        });
        await ImageRetirementScenario.RunAsync(async fixture =>
        {
            var source = fixture.Source; var image = fixture.Image; var png = fixture.PngPath;
            var original = new JournalStore(source.Instance, new()).Read(fixture.Receipt.JournalId).Export();
            var reference = fixture.RegisterNotification();
            var deliveries = new NotificationDeliveryService(source.Instance, source.Database, new(), source.Lease);
            Assert.Equal(1, deliveries.MaterializePending());
            await using var sender = new NotificationSender(source.Instance, source.Database, new(), source.Lease,
                new(source.Instance, source.Database, source.Lease, fixture.Fixture.Base.Resources, fixture.Fixture.Base.Index, new()),
                NotificationWebhookEndpoints.LocalSink(sink.Address));
            var delivery = Assert.Single(sender.ReadQueuedForSource(reference));
            var saved = new NotificationDeliveryCodec(source.Instance, new()).Read(delivery);
            ResourceOperationLease? root = null; Task<NotificationDeliveryState>? sending = null;
            using var cancel = new CancellationTokenSource();
            try
            {
                if (mode != "queued")
                {
                    root = fixture.Fixture.Base.Resources.TryAcquire(saved.Document.OperationId, NotificationSender.Claims).Lease;
                    Assert.NotNull(root);
                    sending = sender.SendOwnedAsync(root!, fixture.Fixture.Base.Resources, delivery, cancel.Token);
                    await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
                    if (mode == "unknown")
                    {
                        cancel.Cancel(); Assert.Equal(NotificationDeliveryState.Unknown, await sending.WaitAsync(TimeSpan.FromSeconds(30)));
                        root!.Dispose(); root = null;
                    }
                }
                var counts = fixture.Fixture.Base.Counts();
                Assert.ThrowsAny<IOException>(() => fixture.Service.RetireImagesPage(Guid.NewGuid(), "image-retirement", image.Key.RunId));
                Assert.Equal(counts, fixture.Fixture.Base.Counts()); Assert.True(File.Exists(png));
                Assert.Equal(image, fixture.Image); fixture.RequireOriginal(original, image);
                if (mode == "unknown")
                {
                    using var retryRoot = fixture.Fixture.Base.Resources.TryAcquire(saved.Document.OperationId, NotificationSender.Claims).Lease;
                    Assert.NotNull(retryRoot);
                    Assert.Equal(NotificationDeliveryState.Unknown, await sender.SendOwnedAsync(retryRoot!, fixture.Fixture.Base.Resources, delivery));
                    Assert.Single(sink.Requests);
                }
            }
            finally
            {
                cancel.Cancel();
                if (sending is not null) _ = await sending.WaitAsync(TimeSpan.FromSeconds(30));
                root?.Dispose();
            }
        }, sink.Address);
    }

    [Theory(DisplayName = "Failed Sql Or Changed Png Or Proof Cannot Authorize Any Additional Image Deletion")]
    [InlineData("rollback")]
    [InlineData("png")]
    [InlineData("proof")]
    public async Task RejectsUnverifiedRetirement(string fault)
    {
        await ImageRetirementScenario.RunAsync(fixture =>
        {
            var source = fixture.Source; var image = fixture.Image; var path = fixture.PngPath;
            var original = new JournalStore(source.Instance, new()).Read(fixture.Receipt.JournalId).Export();
            var operation = Guid.NewGuid();
            if (fault == "png")
            {
                File.WriteAllText(path, "unknown-image-bytes");
                Assert.ThrowsAny<IOException>(() => fixture.Retire(operation));
                Assert.Equal(0, fixture.Fixture.Base.Count("record_content_retirements"));
                Assert.ThrowsAny<IOException>(() => fixture.Images.OpenOriginal(image.Key, image.ResultSha256));
                Assert.Equal(image, fixture.Image); Assert.Equal("unknown-image-bytes", File.ReadAllText(path));
            }
            else
            {
                if (fault == "rollback")
                {
                    var counts = fixture.Fixture.Base.Counts();
                    fixture.Fixture.Base.Execute("CREATE TRIGGER fixture_image_retire BEFORE INSERT ON record_retired_contents " +
                        "BEGIN SELECT RAISE(ABORT,'fixture_image_retire'); END;");
                    try
                    {
                        Assert.Throws<SqliteException>(() => fixture.Retire(operation));
                        Assert.Equal(counts, fixture.Fixture.Base.Counts());
                        Assert.Equal(0, fixture.Fixture.Base.Count("record_content_retirements"));
                        Assert.Equal(image, fixture.Image);
                        using var input = fixture.Images.OpenOriginal(image.Key, image.ResultSha256);
                        Assert.Equal(image.SizeBytes!.Value, input.Length);
                    }
                    finally { fixture.Fixture.Base.Execute("DROP TRIGGER fixture_image_retire;"); }
                }
                var page = fixture.Retire(operation);
                if (fault == "proof")
                {
                    fixture.Fixture.Base.Execute($"UPDATE record_content_retirements SET family_digest='{new string('0', 64)}' WHERE operation_id='{operation:D}';");
                    Assert.ThrowsAny<IOException>(() => RecordRetirementInventory.VerifyReadOnly(source.Instance, source.Database, new()));
                    Assert.ThrowsAny<IOException>(() => fixture.Images.OpenOriginal(image.Key, image.ResultSha256));
                    Assert.True(File.Exists(path));
                    Assert.Equal(FileCleanupResult.Quarantined, fixture.Cleanup(page));
                }
                else
                {
                    Assert.Equal(page, fixture.Retire(operation));
                    Assert.Equal(FileCleanupResult.Cleaned, fixture.Cleanup(page)); Assert.False(File.Exists(path));
                }
            }
            fixture.RequireOriginal(original, image); return Task.CompletedTask;
        });
    }

    [Fact(DisplayName = "An Actual Slow Image Stream Pins Its Kernel File Until The Existing Cleanup Recovery Can Delete It")]
    public async Task HoldsSlowStreams()
    {
        await ImageRetirementScenario.RunAsync(fixture =>
        {
            var image = fixture.Image; var source = fixture.Source; var path = fixture.PngPath;
            using var stream = fixture.Images.OpenOriginal(image.Key, image.ResultSha256);
            var page = fixture.Retire();
            fixture.WithCleanupRoot(page, root =>
            {
                try
                {
                    Assert.Equal(FileCleanupResult.Quarantined,
                        new OwnedCleanupExecutor(source.Instance, new(), source.Database, fixture.Fixture.Base.Index)
                            .Execute(page.CleanupJournalId, source.Lease, root));
                    Assert.True(File.Exists(path)); Assert.Equal(image.SizeBytes!.Value, stream.Length);
                    var buffer = new byte[128]; Assert.True(stream.Read(buffer) > 0);
                }
                finally { stream.Dispose(); }
                var writers = new JournalWriterStore(source.Database);
                CleanupWriterObservation Observe()
                {
                    source.Lease.VerifyHeld(source.Instance); Assert.True(root.IsActive);
                    var actual = writers.ObserveForCurrentExecutor(page.CleanupJournalId);
                    return new(source.Instance.InstanceId, page.CleanupJournalId, actual.IsComplete, actual.State);
                }
                var recovery = new CleanupRecoverySession(source.Instance, page.CleanupJournalId, new(), source.Database, Observe);
                Assert.Equal(FileCleanupResult.Cleaned, new FileCleanupRecovery(recovery).Recover(isExplicitQuarantineRecovery: true));
                Assert.False(File.Exists(path)); return true;
            });
            Assert.Equal(RecordContentAvailability.Retired, fixture.Image.ContentAvailability);
            return Task.CompletedTask;
        });
    }
    private static string ReadMarker(PlanBuilderFixture source, Guid runId)
    {
        using var connection = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT family_sha256 FROM execution_image_asset_catalog WHERE run_id=$run;";
        command.Parameters.AddWithValue("$run", runId.ToString("D"));
        return Assert.IsType<string>(command.ExecuteScalar());
    }

}
