using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Media;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class WindowRuntimeTests
{
    [Theory(DisplayName = "An Actual Run Captures Its Original Window Through The Frozen Nested Broker Before Restoring Configuration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturesFrozenWindow(bool configuration)
    {
        var fixture = new PcStageFixture(configuration: configuration, hooks: false,
            distinctConfiguration: configuration, nativeWindow: true);
        await fixture.ExecuteAsync(async () =>
        {
            var original = File.ReadAllBytes(fixture.Source.Configuration);
            var identity = PhysicalObjectIdentity.Read(fixture.Source.Configuration);
            var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
            Assert.Equal(EngineOutcome.Succeeded, receipt.Facts.Engine);
            Assert.Equal(BusinessOutcome.Succeeded, receipt.Facts.Business);
            var state = fixture.State;
            var saved = ReadCapture(fixture);
            Assert.Equal("captured", saved.Result!.Outcome);
            Assert.Equal("execution_capture_frame_verified", saved.Result.ReasonCode);
            Assert.NotNull(saved.Root); Assert.NotNull(saved.Result.HelperStopped);
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(state.Total.CaptureRuntime!.Broker),
                JsonSerializer.SerializeToElement(saved.Source.HelperImage)));
            Assert.Equal(state.Total.CaptureRuntime.Broker.Path, saved.Root!.NativeRoot.ImagePath, ignoreCase: true);
            Assert.Equal(state.Total.CaptureRuntime.Broker.Sha256, saved.Root.NativeRoot.ImageSha256);
            Assert.Equal(0U, saved.Result.HelperStopped!.ActiveProcesses);
            Assert.Contains(saved.Root.NativeRoot, saved.Result.HelperStopped.AllFamily);
            Assert.All(saved.Result.HelperStopped.AllFamily, member => Assert.Contains(member, saved.Parent!.Family));
            Assert.All(saved.Parent!.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
            Assert.DoesNotContain(state.Jobs, job => job.JobName == saved.State.Binding.HelperJobName);
            Assert.Single(state.Jobs);
            using (var png = new CaptureEvidenceReader(fixture.Source.Instance, fixture.Source.Database, new())
                .OpenCaptured(saved.State.Binding, saved.State.Result!))
                RgbCapturePng.Verify(png, saved.Source.Window!.Width, saved.Source.Window!.Height);
            Assert.Equal(original, File.ReadAllBytes(fixture.Source.Configuration));
            Assert.Equal(identity, PhysicalObjectIdentity.Read(fixture.Source.Configuration));
            var history = new JournalStore(fixture.Source.Instance, new()).ReadAllRevisions(state.Journal.JournalId);
            var measured = JournalCapacityPolicy.Measure(state.Total);
            Assert.True(history.Count <= measured.Revisions);
            Assert.True(history.Sum(item => item.Export().LongLength) <= measured.HistoryBytes);
            Assert.Equal(0, fixture.Consumer.PendingReconciliationCount);
            Assert.Equal(receipt, new AttemptReceiptStore(fixture.Source.Database).Read(receipt.Facts.AttemptId));
        });
    }

    [Fact(DisplayName = "A Launch Disabled Run Can Capture A Fresh Readonly Window While Its Original Skipped Readiness Stays Empty")]
    public async Task CapturesSkippedTarget()
    {
        var fixture = new PcStageFixture(launch: false, external: true, hooks: false, nativeWindow: true);
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
            var saved = ReadCapture(fixture);
            Assert.Equal("captured", saved.Result!.Outcome);
            Assert.Equal(TargetExecutionOutcome.Skipped, saved.Source.Window!.Ownership);
            var stage = new TargetJobEvidence(fixture.Source.Instance, fixture.Source.Database, new())
                .Read<PcStageDocument>(saved.Source.Window!.ReadyArtifact);
            Assert.Empty(stage.ReadyMembers); Assert.Null(stage.TargetJob); Assert.Null(stage.TargetEvidence);
            Assert.Empty(TargetJobCodec.Read(fixture.State.Latest.Root, fixture.State.Total, fixture.State.Attempt.ExecutionPlan.Sha256));
            Assert.True(fixture.GameIsAlive);
            Assert.NotEqual(saved.Source.Window!.Process, saved.Root!.NativeRoot);
            Assert.DoesNotContain(saved.Source.Window!.Process, saved.Parent!.Family);
        });
    }

    [Fact(DisplayName = "Actual Capture Metadata History And Gc References Do Not Open The Original Png")]
    public async Task ReadsImageMetadata()
    {
        var fixture = new PcStageFixture(hooks: false, nativeWindow: true);
        await fixture.ExecuteAsync(async () =>
        {
            _ = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            var saved = ReadCapture(fixture);
            Assert.Equal("captured", saved.Result!.Outcome);
            var png = Assert.IsType<BlobReference>(saved.Result.Png);
            var state = fixture.State;
            var path = Path.Combine(fixture.Source.Instance.RuntimeRoot, png.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.NotNull(AttemptPreparedBinding.Read(fixture.Source.Instance, new(), fixture.Source.Database, state.Journal.JournalId));
                var refs = new PlanReferenceClosure(fixture.Source.Instance, new(), new(fixture.Source.Instance, new()), fixture.Source.Database)
                    .Read(state.Journal, AttemptPreparedBinding.Reference(fixture.Source.Instance, state.Journal));
                Assert.Contains(png, refs);
                Assert.True(new BlobReferenceIndex(fixture.Source.Instance, new(), fixture.Source.Database, new(fixture.Source.Instance, new())).IsReferenced(png));
                Assert.ThrowsAny<IOException>(() => new CaptureEvidenceReader(fixture.Source.Instance, fixture.Source.Database, new())
                    .OpenCaptured(saved.State.Binding, saved.State.Result!));
            }
            using var opened = new CaptureEvidenceReader(fixture.Source.Instance, fixture.Source.Database, new())
                .OpenCaptured(saved.State.Binding, saved.State.Result!);
            Assert.Equal(png.SizeBytes, opened.Length);
        });
    }

    private static CaptureSavedEvidence ReadCapture(PcStageFixture fixture)
    {
        var state = fixture.State;
        return Assert.Single(new CaptureEvidenceReader(fixture.Source.Instance, fixture.Source.Database, new())
            .ReadMetadata(state.Journal, state.Attempt, state.Total,
                new JournalStore(fixture.Source.Instance, new()).ReadAllRevisions(state.Journal.JournalId)));
    }
}
