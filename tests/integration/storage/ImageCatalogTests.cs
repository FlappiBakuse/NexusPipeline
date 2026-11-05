using System.Text.Json;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Records;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Media;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ImageCatalogTests
{
    [Fact(DisplayName = "An Actual Attempt Keeps Its Original Images Before Any Final Record And Metadata Publication Never Opens Png")]
    public async Task PagesAcceptedImages()
    {
        var fixture = new PcStageFixture(hooks: false, nativeWindow: true);
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            var source = fixture.Source;
            var original = new JournalStore(source.Instance, new()).Read(receipt.JournalId).Export();
            var projection = new ImageCatalogProjection(source.Instance, source.Database, new(), source.Lease);
            projection.OnCommitted(receipt); projection.OnCommitted(receipt);
            var reader = new ImageCatalogReader(source.Instance, source.Database, new());
            var row = Assert.Single(reader.ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId)).Items);
            Assert.Equal(RunImageOutcome.Captured, row.Outcome);
            Assert.Equal(receipt.Facts.AttemptId, row.Key.AttemptId);
            Assert.NotNull(row.ImageSha256); Assert.NotNull(row.RootSha256);
            Assert.Equal(0, fixture.Base.Count("run_records")); Assert.Equal(0, fixture.Base.Count("record_assets"));
            var path = Path.Combine(source.Instance.RuntimeRoot, "blobs", "content", row.ImageSha256 + ".bin");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Equal(row, Assert.Single(reader.ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId)).Items));
                new ExecutionRecordProjection(source.Instance, source.Database, new(), source.Lease,
                    new(source.Instance, source.Database, new())).OnCommitted(receipt);
                Assert.Equal(row, Assert.Single(reader.ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId)).Items));
                Assert.ThrowsAny<IOException>(() => reader.OpenOriginal(row.Key, row.ResultSha256));
                Assert.Equal(1, fixture.Base.Count("execution_image_asset_catalog"));
                using var connection = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true);
                using var transaction = connection.BeginTransaction(deferred: true);
                var published = Assert.IsType<RecordAsset>(NotificationReceiptAssets.ReadPublished(connection, transaction, receipt.Facts.RunId));
                Assert.Equal(row.ImageSha256, published.Sha256); Assert.Equal(row.SizeBytes, published.SizeBytes);
            }
            using (var opened = reader.OpenOriginal(row.Key, row.ResultSha256))
                RgbCapturePng.Verify(opened, row.Width!.Value, row.Height!.Value);
            Assert.ThrowsAny<IOException>(() => reader.OpenOriginal(row.Key, new string('0', 64)));
            Assert.Equal(1, fixture.Base.Count("run_records")); Assert.Equal(1, fixture.Base.Count("record_assets"));
            Assert.Equal(original, new JournalStore(source.Instance, new()).Read(receipt.JournalId).Export());
            Assert.Equal(receipt, new AttemptReceiptStore(source.Database).Read(receipt.Facts.AttemptId));
            Assert.Contains(reader.ExplainPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId)),
                line => line.Contains("SEARCH execution_image_entries", StringComparison.Ordinal));
            Assert.Null(projection.ReconcilePage()); Assert.Null(projection.ReconcileAssetsPage());
        });
    }

    [Theory(DisplayName = "An Actual Image Projection Rolls Back As One Catalog And Cannot Grant A Tampered Sql Png Identity")]
    [InlineData("rollback")]
    [InlineData("entry")]
    public async Task RevertsImageProjection(string fault)
    {
        var fixture = new PcStageFixture(hooks: false, nativeWindow: true);
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync()); var source = fixture.Source;
            var original = new JournalStore(source.Instance, new()).Read(receipt.JournalId).Export();
            var projection = new ImageCatalogProjection(source.Instance, source.Database, new(), source.Lease);
            if (fault == "rollback")
            {
                fixture.Base.Execute("CREATE TRIGGER fixture_image_projection BEFORE INSERT ON execution_image_entries " +
                    "BEGIN SELECT RAISE(ABORT,'fixture_image_projection'); END;");
                try
                {
                    Assert.Throws<SqliteException>(() => projection.OnCommitted(receipt));
                    Assert.Equal(0, fixture.Base.Count("execution_image_catalog")); Assert.Equal(0, fixture.Base.Count("execution_image_entries"));
                    Assert.Equal(0, fixture.Base.Count("record_assets"));
                }
                finally { fixture.Base.Execute("DROP TRIGGER fixture_image_projection;"); }
            }
            projection.OnCommitted(receipt);
            var reader = new ImageCatalogReader(source.Instance, source.Database, new());
            var row = Assert.Single(reader.ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId)).Items);
            if (fault == "entry")
            {
                fixture.Base.Execute("UPDATE execution_image_entries SET png_sha256='" + new string('0', 64) +
                    $"' WHERE attempt_id='{receipt.Facts.AttemptId:D}' AND position=0;");
                Assert.ThrowsAny<IOException>(() => reader.ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId)));
                Assert.ThrowsAny<IOException>(() => reader.OpenOriginal(row.Key, row.ResultSha256));
                Assert.ThrowsAny<IOException>(() => projection.OnCommitted(receipt));
            }
            else
            {
                var records = new ExecutionRecordProjection(source.Instance, source.Database, new(), source.Lease,
                    new(source.Instance, source.Database, new()));
                fixture.Base.Execute("CREATE TRIGGER fixture_image_assets BEFORE INSERT ON execution_image_asset_catalog " +
                    "BEGIN SELECT RAISE(ABORT,'fixture_image_assets'); END;");
                try
                {
                    Assert.Throws<SqliteException>(() => records.OnCommitted(receipt));
                    Assert.Equal(0, fixture.Base.Count("run_records")); Assert.Equal(0, fixture.Base.Count("record_assets"));
                    Assert.Equal(0, fixture.Base.Count("execution_image_asset_catalog"));
                    Assert.Equal(row, Assert.Single(reader.ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId)).Items));
                }
                finally { fixture.Base.Execute("DROP TRIGGER fixture_image_assets;"); }
                records.OnCommitted(receipt); records.OnCommitted(receipt);
                Assert.Equal(1, fixture.Base.Count("run_records")); Assert.Equal(1, fixture.Base.Count("record_assets"));
                Assert.Equal(1, fixture.Base.Count("execution_image_asset_catalog"));
                Assert.Equal(row, Assert.Single(reader.ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId)).Items));
            }
            Assert.Equal(original, new JournalStore(source.Instance, new()).Read(receipt.JournalId).Export());
            Assert.Equal(receipt, new AttemptReceiptStore(source.Database).Read(receipt.Facts.AttemptId));
        });
    }

    [Fact(DisplayName = "A Real Waiting Retry Keeps Its Old Attempt Image Header Without Inventing An Image Or Final Run Record")]
    public async Task KeepsRetryHeaders()
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("retry-interrupted",
            retrySafe: true, retryApproved: true, maximumAttempts: 2);
        var source = fixture.Source; var first = await fixture.RunInterruptedAsync();
        var records = new ExecutionRecordProjection(source.Instance, source.Database, new(), source.Lease,
            new(source.Instance, source.Database, new()));
        records.OnCommitted(first);
        var reader = new ImageCatalogReader(source.Instance, source.Database, new());
        Assert.Empty(reader.ReadPage(new(first.Facts.RunId, first.Facts.AttemptId)).Items);
        Assert.Equal(1, fixture.Base.Count("execution_image_catalog")); Assert.Equal(0, fixture.Base.Count("run_records"));
        var retries = new ExecutionRetryService(source.Instance, source.Database, new(), source.Lease, fixture.Service);
        using var pending = Assert.IsType<PreparedExecutionRetry>(retries.Prepare(first.Facts.RunId, first.Facts.AttemptId, DateTimeOffset.UtcNow));
        _ = retries.Start(pending, DateTimeOffset.UtcNow);
        var second = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        records.OnCommitted(second);
        Assert.Equal(first.Facts.RunId, second.Facts.RunId); Assert.NotEqual(first.Facts.AttemptId, second.Facts.AttemptId);
        Assert.Empty(reader.ReadPage(new(first.Facts.RunId, first.Facts.AttemptId)).Items);
        Assert.Empty(reader.ReadPage(new(second.Facts.RunId, second.Facts.AttemptId)).Items);
        Assert.Equal(2, fixture.Base.Count("execution_image_catalog")); Assert.Equal(1, fixture.Base.Count("execution_image_asset_catalog"));
        Assert.Equal(0, fixture.Base.Count("record_assets"));
        Assert.Equal(first, new AttemptReceiptStore(source.Database).Read(first.Facts.AttemptId));
    }

    [Fact(DisplayName = "An Actual Window Png Reaches A Local Notification Only After The Final Record Producer Publishes Its Source Relation")]
    public async Task PublishesNotificationSources()
    {
        await using var sink = new NotificationHttpSink((_, _) => Task.FromResult(new NotificationSinkReply(200, "")));
        var fixture = new PcStageFixture(accept: false, hooks: false, nativeWindow: true);
        await fixture.ExecuteAsync(async () =>
        {
            var source = fixture.Source; var task = Guid.NewGuid();
            var catalog = new NotificationCatalogService(source.Instance, source.Database, source.Lease,
                fixture.Base.Resources, fixture.Base.Index, new());
            using var url = new SecretValue(new Uri(sink.Address, "hook").AbsoluteUri);
            var channel = NotificationChannelConfiguration.Default with { WebhookKind = NotificationWebhookKind.Generic,
                ScreenshotEnabled = true, WebhookTemplate = "{\"image\":{imageBase64},\"text\":{text}}" };
            await catalog.SaveAsync(Guid.NewGuid(), "local:image-catalog", 0, task, 0,
                new("actual captured image", "", NotificationTaskKind.Webhook, true, channel),
                [new(NotificationSecretField.WebhookUrl, 0, SecretDisposition.Set, url)]);
            var definitions = source.Definitions.Read();
            var binding = definitions.Bindings.Single(value => value.Id == source.BindingA) with { NotifyEnabled = true, NotificationTaskIds = [task] };
            var changes = new AutomationChanges(definitions.Revision, [], [], [], [binding], [], [], []);
            source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), PcStageFixture.Subject, changes), changes, DateTimeOffset.UtcNow);
            _ = fixture.Base.Service.Start(Guid.NewGuid(), PcStageFixture.Subject,
                source.Request(new BindingTarget(source.BindingA)), DateTimeOffset.UtcNow);
            var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            var finality = new ExecutionRunFinality(source.Instance, source.Database, new());
            new ExecutionRecordProjection(source.Instance, source.Database, new(), source.Lease, finality).OnCommitted(receipt);
            var image = Assert.Single(new ImageCatalogReader(source.Instance, source.Database, new())
                .ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId)).Items);
            new ExecutionNotificationRegistration(source.Instance, source.Database, new(), source.Lease, finality).OnCommitted(receipt);
            BlobReference sourceReference;
            using (var connection = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true))
            using (var transaction = connection.BeginTransaction(deferred: true))
            {
                sourceReference = Assert.IsType<BlobReference>(NotificationReceiptRows.Find(connection, transaction, receipt.Facts.AttemptId));
                var snapshot = new NotificationReceiptCodec(source.Instance, source.Database, new()).Read(sourceReference, connection, transaction);
                Assert.Equal(image.ImageSha256, snapshot.Document.Screenshot!.Sha256);
                Assert.Equal(image.SizeBytes, snapshot.Document.Screenshot.SizeBytes);
                transaction.Commit();
            }
            var delivery = new NotificationDeliveryService(source.Instance, source.Database, new(), source.Lease);
            Assert.Equal(1, delivery.MaterializePending());
            await using var sender = new NotificationSender(source.Instance, source.Database, new(), source.Lease,
                new(source.Instance, source.Database, source.Lease, fixture.Base.Resources, fixture.Base.Index, new()),
                NotificationWebhookEndpoints.LocalSink(sink.Address));
            var reference = Assert.Single(sender.ReadQueuedForSource(sourceReference));
            var saved = new NotificationDeliveryCodec(source.Instance, new()).Read(reference);
            using var root = fixture.Base.Resources.TryAcquire(saved.Document.OperationId, NotificationSender.Claims).Lease;
            Assert.NotNull(root);
            Assert.Equal(NotificationDeliveryState.Succeeded, await sender.SendOwnedAsync(root, fixture.Base.Resources, reference));
            var request = Assert.Single(sink.Requests);
            var bytes = File.ReadAllBytes(Path.Combine(source.Instance.RuntimeRoot, "blobs", "content", image.ImageSha256 + ".bin"));
            var base64 = Convert.ToBase64String(bytes);
            Assert.InRange(request.BodyBytes, 1, 65536);
            using var packet = JsonDocument.Parse(request.Prefix);
            Assert.Equal(base64, packet.RootElement.GetProperty("image").GetString());
            Assert.Equal(receipt, new AttemptReceiptStore(source.Database).Read(receipt.Facts.AttemptId));
        });
    }
}
