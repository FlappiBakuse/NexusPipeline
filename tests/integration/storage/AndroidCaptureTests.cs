using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Media;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Records;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class AndroidCaptureTests
{
    [Theory(DisplayName = "The Frozen Generic Tool Captures Exact Binary Bytes Inside The Original Main Family Before Post And Restore")]
    [InlineData("rgb", true, 2)]
    [InlineData("rgba", false, 6)]
    public async Task CapturesFrozenBytes(
        string mode, bool launch, int color)
    {
        var fixture = new AndroidRuntimeFixture(configuration: true, hooks: true, launch: launch,
            forceClose: false, capture: true, captureMode: mode);
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = await fixture.RunAsync();
            Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
            var saved = ReadCapture(fixture); var state = fixture.State;
            Assert.Null(saved.Source.Window); Assert.NotNull(saved.Source.Android);
            Assert.Equal("captured", saved.Result!.Outcome);
            Assert.Equal(new CapturePngInfo(3, 2, color), saved.Result.Image);
            var target = Assert.Single(state.Total.Steps).Target!.Android!;
            Assert.Equal(target.Tool.Path, saved.Source.HelperImage.Path, ignoreCase: true);
            Assert.Equal(target.Tool.Sha256, saved.Root!.NativeRoot.ImageSha256);
            Assert.Equal(target.Endpoint, saved.Source.Android!.Endpoint);
            Assert.All(saved.Result.HelperStopped!.AllFamily, member =>
            {
                Assert.Contains(member, saved.Parent!.Family);
                Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member));
            });
            var observations = Directory.GetFiles(Path.Combine(fixture.Directory, "observed"), "*.json")
                .Select(path => JsonDocument.Parse(File.ReadAllBytes(path))).ToArray();
            try
            {
                var capture = Assert.Single(observations, document =>
                    document.RootElement.GetProperty("arguments").EnumerateArray().Any(value => value.GetString() == "exec-out"));
                Assert.Equal(new[] { "-s", AndroidRuntimeFixture.Endpoint, "exec-out", "screencap", "-p" },
                    capture.RootElement.GetProperty("arguments").EnumerateArray().Select(value => value.GetString()).ToArray());
                Assert.Equal(saved.Root.NativeRoot.ProcessId, capture.RootElement.GetProperty("processId").GetInt32());
                if (!launch) Assert.Single(observations);
            }
            finally { foreach (var document in observations) document.Dispose(); }
            using (var png = new CaptureEvidenceReader(fixture.Source.Instance, fixture.Source.Database, new())
                .OpenCaptured(saved.State.Binding, saved.State.Result!))
            {
                Assert.Equal(saved.Result.Image, BoundedCapturePng.Verify(png, allowRgba: true));
                using var bytes = new MemoryStream(); png.CopyTo(bytes);
                Assert.Equal(File.ReadAllBytes(Path.Combine(fixture.Directory, "fixture-screenshot.png")), bytes.ToArray());
            }
            var startup = Assert.Single(AndroidStartupCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
            Assert.Equal(launch ? TargetExecutionOutcome.ControlledReady : TargetExecutionOutcome.Skipped, startup.Outcome);
            Assert.Equal(launch ? "execution_android_controlled_ready" : "execution_android_launch_disabled", startup.ReasonCode);
            Assert.True(File.Exists(fixture.Marker + ".post")); fixture.VerifyOriginal();
            var history = new JournalStore(fixture.Source.Instance, new()).ReadAllRevisions(state.Journal.JournalId);
            var bound = JournalCapacityPolicy.Measure(state.Total);
            Assert.True(history.Count <= bound.Revisions);
            Assert.True(history.Sum(value => value.Export().LongLength) <= bound.HistoryBytes);
            Assert.Equal(state.Jobs.Length + AndroidControlCapacity.Derive(Assert.Single(state.Total.Steps)).MaximumCommands + 2,
                bound.Jobs);
        });
    }

    [Theory(DisplayName = "A Failed Or Incomplete Binary Read Never Invents An Image Or Unknown Dimensions")]
    [InlineData("offline")]
    [InlineData("truncated")]
    [InlineData("hang")]
    [InlineData("overflow")]
    [InlineData("compressedTail")]
    public async Task RejectsIncompleteImages(string mode)
    {
        var fixture = new AndroidRuntimeFixture(forceClose: false, capture: true, captureMode: mode);
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = await fixture.RunAsync();
            Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
            var saved = ReadCapture(fixture);
            Assert.Equal("unavailable", saved.Result!.Outcome); Assert.Null(saved.Result.Png); Assert.Null(saved.Result.Image);
            Assert.NotNull(saved.Result.HelperStopped); Assert.NotNull(saved.Parent);
            Assert.All(saved.Result.HelperStopped!.AllFamily, member =>
            {
                Assert.Contains(member, saved.Parent!.Family);
                Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member));
            });
            new ImageCatalogProjection(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Source.Lease).OnCommitted(receipt);
            var item = Assert.Single(new ImageCatalogReader(fixture.Source.Instance, fixture.Source.Database, new())
                .ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId)).Items);
            Assert.Equal(RunImageOutcome.Unavailable, item.Outcome); Assert.Null(item.Width); Assert.Null(item.Height);
            Assert.Null(item.ImageSha256);
        });
    }

    [Fact(DisplayName = "An Actual Rgba Image Keeps Metadata Readable While Locked And Retires Through The Original Cleanup After Final Publication")]
    public async Task RetiresPublishedImages()
    {
        var fixture = new AndroidRuntimeFixture(configuration: true, forceClose: false, capture: true, captureMode: "rgba");
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = await fixture.RunAsync(); var source = fixture.Source;
            var records = new ExecutionRecordProjection(source.Instance, source.Database, new(), source.Lease,
                new(source.Instance, source.Database, new()));
            records.OnCommitted(receipt);
            var reader = new ImageCatalogReader(source.Instance, source.Database, new());
            var image = Assert.Single(reader.ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId)).Items);
            var saved = ReadCapture(fixture); var path = Path.Combine(source.Instance.RuntimeRoot, "blobs", "content", image.ImageSha256 + ".bin");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Equal(image, Assert.Single(reader.ReadPage(new(image.Key.RunId, image.Key.AttemptId)).Items));
                records.OnCommitted(receipt);
                Assert.Contains(saved.Result!.Png!, new CaptureEvidenceReader(source.Instance, source.Database, new())
                    .ReadReferences(fixture.State.Journal, fixture.State.Attempt, fixture.State.Total,
                        new JournalStore(source.Instance, new()).ReadAllRevisions(receipt.JournalId)));
                Assert.ThrowsAny<IOException>(() => reader.OpenOriginal(image.Key, image.ResultSha256));
            }
            var retirement = new ContentRetirementService(source.Instance, source.Database, new(), source.Lease,
                fixture.Base.Resources, fixture.Base.Index);
            var page = Assert.IsType<ContentRetirementPage>(retirement.RetireImagesPage(Guid.NewGuid(),
                "generic-image-retirement", receipt.Facts.RunId, limit: 1));
            var journal = new RecoveryJournalRegistry(source.Database).ReadJournal(page.CleanupJournalId);
            var plan = new CleanupPlanCodec(source.Instance, new(), new(source.Instance, new())).Read(
                new(journal.PlanPath, new FileInfo(Path.Combine(source.Instance.RuntimeRoot, journal.PlanPath)).Length, journal.PlanDigest));
            var claims = plan.Items.Select(item => new ResourceClaim(CleanupCandidateFiles.ResourceKey(item.Blob), ResourceClaimMode.Mutation)).ToArray();
            using (var root = fixture.Base.Resources.TryAcquire(page.OperationId, claims).Lease)
            {
                Assert.NotNull(root);
                Assert.Equal(FileCleanupResult.Cleaned, new OwnedCleanupExecutor(source.Instance, new(), source.Database, fixture.Base.Index)
                    .Execute(page.CleanupJournalId, source.Lease, root!));
            }
            Assert.False(File.Exists(path));
            Assert.Equal(RecordContentAvailability.Retired, Assert.Single(reader.ReadPage(new(image.Key.RunId, image.Key.AttemptId)).Items).ContentAvailability);
            Assert.Throws<RetiredContentException>(() => reader.OpenOriginal(image.Key, image.ResultSha256));
            records.OnCommitted(receipt);
            Assert.Contains(new RecordAssetStore(source.Database, new(source.Instance, new())).ReadForRun(receipt.Facts.RunId).Items,
                asset => asset.Kind == RecordAssetKind.Screenshot && asset.Sha256 == image.ImageSha256);
            source.Lease.Dispose();
            using var reopened = InstanceLease.Acquire(source.Instance, new());
            var database = StateDatabase.OpenExisting(source.Instance, Path.Combine(source.Root, "generic-image-reopen"),
                new JournalStartupAdmission(source.Instance, new()));
            var cold = new ImageCatalogReader(source.Instance, database, new());
            Assert.Equal(RecordContentAvailability.Retired, Assert.Single(cold.ReadPage(new(image.Key.RunId, image.Key.AttemptId)).Items).ContentAvailability);
            Assert.Throws<RetiredContentException>(() => cold.OpenOriginal(image.Key, image.ResultSha256));
        });
    }

    internal static CaptureSavedEvidence ReadCapture(AndroidRuntimeFixture fixture)
    {
        var state = fixture.State;
        return Assert.Single(new CaptureEvidenceReader(fixture.Source.Instance, fixture.Source.Database, new())
            .ReadMetadata(state.Journal, state.Attempt, state.Total,
                new JournalStore(fixture.Source.Instance, new()).ReadAllRevisions(state.Journal.JournalId)));
    }
}
