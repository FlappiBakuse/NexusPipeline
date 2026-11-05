using System.Runtime.Versioning;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Records;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RetirementGuardTests
{
    [Fact(DisplayName = "A Committed Receipt Without Its Actual Final Run Projection Cannot Mint Retirement Authority")]
    public async Task RequiresFinalProjection()
    {
        using var fixture = await RecordRetirementFixture.CreateAsync(publishRecord: false);
        Assert.ThrowsAny<IOException>(() => fixture.Retirement.RetireLogsPage(Guid.NewGuid(), "retirement-fixture", fixture.Receipt.Facts.RunId));
        Assert.Equal(0, fixture.Hooks.Base.Count("record_content_retirements"));
        new ExecutionRecordProjection(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Source.Lease,
            new(fixture.Source.Instance, fixture.Source.Database, new())).OnCommitted(fixture.Receipt);
        Assert.NotNull(fixture.Retirement.RetireLogsPage(Guid.NewGuid(), "retirement-fixture", fixture.Receipt.Facts.RunId));
    }

    [Fact(DisplayName = "A Real Waiting Retry Cannot Retire Its Stopped First Attempt Or Consume The New Retirement Operation")]
    public async Task PreservesWaitingRetries()
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("retry-interrupted",
            retrySafe: true, retryApproved: true, maximumAttempts: 2);
        var receipt = await fixture.RunInterruptedAsync(); var source = fixture.Source;
        Assert.False(new ExecutionRunFinality(source.Instance, source.Database, new()).IsFinal(receipt));
        var observed = new JournalWriterStore(source.Database).ObserveForCurrentExecutor(receipt.JournalId);
        Assert.True(observed.IsComplete); Assert.Equal(NexusPipeline.Core.Execution.WriterState.Stopped, observed.State);
        Assert.Equal(0, fixture.Base.Count("run_records"));
        Assert.Equal(1, fixture.Base.Count("execution_retry_intents"));
        Assert.Equal(0, fixture.Base.Count("execution_retry_outcomes"));
        var counts = fixture.Base.Counts();
        var service = new ContentRetirementService(source.Instance, source.Database, new(), source.Lease, fixture.Base.Resources, fixture.Base.Index);
        var rejected = Assert.Throws<IOException>(() => service.RetireLogsPage(Guid.NewGuid(), "retirement-fixture", receipt.Facts.RunId));
        Assert.Equal("record_retirement_run_pending", rejected.Message);
        Assert.Equal(counts, fixture.Base.Counts());
        Assert.Equal(0, fixture.Base.Count("record_content_retirements"));
        Assert.Equal(receipt, new AttemptReceiptStore(source.Database).Read(receipt.Facts.AttemptId));
    }

    [Theory(DisplayName = "Actual Unexpanded Or Queued Notification Work Protects Its Source Run")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreservesNotificationSources(bool expand)
    {
        using var fixture = new NotificationReceiptFixture(); await fixture.Execute();
        if (expand) Assert.Equal(1, fixture.Delivery.MaterializePending());
        Assert.False(fixture.Delivery.IsSettledForRun(fixture.Receipt.Facts.RunId));
        var source = fixture.Source;
        var service = new ContentRetirementService(source.Instance, source.Database, new(), source.Lease, fixture.Base.Resources, fixture.Base.Index);
        Assert.ThrowsAny<IOException>(() => service.RetireLogsPage(Guid.NewGuid(), "retirement-fixture", fixture.Receipt.Facts.RunId));
        Assert.Equal(0, fixture.Base.Count("record_content_retirements"));
        Assert.Equal(fixture.Receipt, new AttemptReceiptStore(source.Database).Read(fixture.Receipt.Facts.AttemptId));
    }

    [Fact(DisplayName = "Another Actual Asset Reference Retains Identical Bytes After Their Display Log Owner Retires")]
    public async Task KeepsSharedAssets()
    {
        using var fixture = await RecordRetirementFixture.CreateAsync();
        var part = fixture.Logs.Describe(fixture.Items[0].Key).Parts[0];
        var blobs = new ImmutableBlobStore(fixture.Source.Instance, new());
        var assets = new RecordAssetStore(fixture.Source.Database, blobs);
        var asset = new RecordAsset(fixture.Receipt.Facts.RunId, part.Sha256, part.SizeBytes, RecordAssetKind.Attachment);
        assets.AttachVerified(asset);
        var page = Assert.IsType<ContentRetirementPage>(fixture.Retirement.RetireLogsPage(Guid.NewGuid(), "retirement-fixture", fixture.Receipt.Facts.RunId));
        Assert.Equal(NexusPipeline.Core.Configuration.FileCleanupResult.Cleaned, fixture.Cleanup(page));
        using var input = assets.OpenVerified(asset);
        Assert.Equal(asset.SizeBytes, input.Length);
        Assert.Contains(fixture.Plan(page).Items, item => item.Blob.Sha256 == asset.Sha256);
        Assert.True(new BlobReferenceIndex(fixture.Source.Instance, new(), fixture.Source.Database, blobs)
            .IsReferenced(new($"blobs/content/{asset.Sha256}.bin", asset.SizeBytes, asset.Sha256)));
        Assert.Equal(fixture.Receipt, new AttemptReceiptStore(fixture.Source.Database).Read(fixture.Receipt.Facts.AttemptId));
    }

    [Fact(DisplayName = "An Actual Recovery Block Cannot Be Replaced By Closed Writer Evidence Alone")]
    public async Task RequiresVerifiedRecovery()
    {
        using var fixture = await RecordRetirementFixture.CreateAsync();
        fixture.Hooks.Base.Index.BlockUnknown("retirement_fixture_recovery_unknown");
        Assert.ThrowsAny<IOException>(() => fixture.Retirement.RetireLogsPage(Guid.NewGuid(), "retirement-fixture", fixture.Receipt.Facts.RunId));
        Assert.Equal(0, fixture.Hooks.Base.Count("record_content_retirements"));
        Assert.All(fixture.Items, item => Assert.Equal(RecordContentAvailability.Available, item.ContentAvailability));
    }
}
