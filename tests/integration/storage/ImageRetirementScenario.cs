using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Records;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Records;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class ImageRetirementScenario(PcStageFixture fixture, AttemptReceipt receipt)
{
    internal PcStageFixture Fixture => fixture;
    internal PlanBuilderFixture Source => fixture.Source;
    internal AttemptReceipt Receipt => receipt;
    internal ImageCatalogReader Images => new(Source.Instance, Source.Database, new());
    internal ContentRetirementService Service => new(Source.Instance, Source.Database, new(),
        Source.Lease, fixture.Base.Resources, fixture.Base.Index);
    internal ImageCatalogItem Image => Assert.Single(Images.ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId)).Items);
    internal string PngPath => Path.Combine(Source.Instance.RuntimeRoot, "blobs", "content", Image.ImageSha256 + ".bin");

    internal static async Task RunAsync(Func<ImageRetirementScenario, Task> action, Uri? notification = null)
    {
        var fixture = new PcStageFixture(accept: notification is null, hooks: false, nativeWindow: true);
        await fixture.ExecuteAsync(async () =>
        {
            if (notification is not null) await PrepareNotification(fixture, notification);
            var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            new ExecutionRecordProjection(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Source.Lease,
                new(fixture.Source.Instance, fixture.Source.Database, new())).OnCommitted(receipt);
            var observed = new JournalWriterStore(fixture.Source.Database).ObserveForCurrentExecutor(receipt.JournalId);
            Assert.True(observed.IsComplete); Assert.Equal(WriterState.Stopped, observed.State);
            await action(new(fixture, receipt));
        });
    }

    private static async Task PrepareNotification(PcStageFixture fixture, Uri sink)
    {
        var source = fixture.Source; var task = Guid.NewGuid();
        var catalog = new NotificationCatalogService(source.Instance, source.Database, source.Lease,
            fixture.Base.Resources, fixture.Base.Index, new());
        using var url = new SecretValue(sink.AbsoluteUri);
        await catalog.SaveAsync(Guid.NewGuid(), "local:image-retirement", 0, task, 0,
            new("image retirement", "", NotificationTaskKind.Webhook, true, NotificationChannelConfiguration.Default with {
                WebhookKind = NotificationWebhookKind.Generic, ScreenshotEnabled = true,
                WebhookTemplate = "{\"image\":{imageBase64}}", TimeoutSeconds = 120 }),
            [new(NotificationSecretField.WebhookUrl, 0, SecretDisposition.Set, url)]);
        var definitions = source.Definitions.Read();
        var binding = definitions.Bindings.Single(value => value.Id == source.BindingA) with { NotifyEnabled = true, NotificationTaskIds = [task] };
        var changes = new AutomationChanges(definitions.Revision, [], [], [], [binding], [], [], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), PcStageFixture.Subject, changes),
            changes, DateTimeOffset.UtcNow);
        _ = fixture.Base.Service.Start(Guid.NewGuid(), PcStageFixture.Subject,
            source.Request(new BindingTarget(source.BindingA)), DateTimeOffset.UtcNow);
    }

    internal ContentRetirementPage Retire(Guid? operation = null) =>
        Assert.IsType<ContentRetirementPage>(Service.RetireImagesPage(operation ?? Guid.NewGuid(),
            "image-retirement", receipt.Facts.RunId, limit: 1));

    internal FrozenFileCleanup Plan(ContentRetirementPage page)
    {
        var registered = new RecoveryJournalRegistry(Source.Database).ReadJournal(page.CleanupJournalId);
        return new CleanupPlanCodec(Source.Instance, new(), new(Source.Instance, new())).Read(
            new(registered.PlanPath, new FileInfo(Path.Combine(Source.Instance.RuntimeRoot, registered.PlanPath)).Length, registered.PlanDigest));
    }

    internal T WithCleanupRoot<T>(ContentRetirementPage page, Func<ResourceOperationLease, T> action)
    {
        var claims = Plan(page).Items.Select(item => new ResourceClaim(CleanupCandidateFiles.ResourceKey(item.Blob),
            ResourceClaimMode.Mutation)).ToArray();
        using var root = fixture.Base.Resources.TryAcquire(page.OperationId, claims).Lease;
        Assert.NotNull(root);
        using var borrow = root!.Borrow(fixture.Base.Resources, page.OperationId, claims);
        return borrow.Use(() => action(root));
    }

    internal FileCleanupResult Cleanup(ContentRetirementPage page) => WithCleanupRoot(page, root =>
        new OwnedCleanupExecutor(Source.Instance, new(), Source.Database, fixture.Base.Index)
            .Execute(page.CleanupJournalId, Source.Lease, root));

    internal BlobReference RegisterNotification()
    {
        new ExecutionNotificationRegistration(Source.Instance, Source.Database, new(), Source.Lease,
            new(Source.Instance, Source.Database, new())).OnCommitted(receipt);
        using var connection = DatabaseConnections.Open(Source.Database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var reference = Assert.IsType<BlobReference>(NotificationReceiptRows.Find(connection, transaction, receipt.Facts.AttemptId));
        transaction.Commit(); return reference;
    }

    internal void RequireOriginal(byte[] journal, ImageCatalogItem image)
    {
        Assert.Equal(journal, new JournalStore(Source.Instance, new()).Read(receipt.JournalId).Export());
        Assert.Equal(receipt, new AttemptReceiptStore(Source.Database).Read(receipt.Facts.AttemptId));
        Assert.Equal(1, fixture.Base.Count("execution_image_asset_catalog"));
        Assert.Contains(new RecordAssetStore(Source.Database, new(Source.Instance, new())).ReadForRun(receipt.Facts.RunId).Items,
            asset => asset.Kind == RecordAssetKind.Screenshot && asset.Sha256 == image.ImageSha256);
    }
}
