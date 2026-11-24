using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Media;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Records;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class MuMuRuntimeTests
{
    [Theory(DisplayName = "The Explicit Paired Tool Uses Three Ordered Real Jobs And Captures The Original Bytes Within The Main Family")]
    [InlineData("rgb", true, 2)]
    [InlineData("rgba", false, 6)]
    public async Task CapturesOrderedJobs(
        string mode, bool launch, int color)
    {
        var fixture = new AndroidRuntimeFixture(mumu: true, pairedCapture: true, capture: true,
            configuration: true, hooks: true, launch: launch, forceClose: false, captureMode: mode);
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = await fixture.RunAsync();
            Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
            var state = fixture.State; var saved = AndroidCaptureTests.ReadCapture(fixture);
            Assert.Null(saved.Source.Window); Assert.NotNull(saved.Source.Android!.MuMu);
            Assert.Equal("captured", saved.Result!.Outcome);
            Assert.Equal(new CapturePngInfo(3, 2, color), saved.Result.Image);
            var target = Assert.Single(state.Total.Steps).Target!.Android!;
            Assert.Equal(state.Total.CaptureRuntime!.Broker.Path, saved.Source.HelperImage.Path, ignoreCase: true);
            Assert.Equal(target.Tool.Sha256, saved.Source.Android.MuMu!.ManagerImageSha256);
            Assert.Equal(target.Mapping!.Index, saved.Source.Android.MuMu.Index);
            Assert.NotEqual(target.Tool.Path, target.ScreenshotTool!.Path);
            Assert.Equal(new[] { 1, 2, 3 }, saved.Commands.Select(command => command.Root.Sequence));
            Assert.Equal(new[] { "beforeMapping", "image", "afterMapping" }, saved.Commands.Select(command => command.Root.Purpose));
            Assert.Equal(3, saved.Commands.Select(command => command.Root.JobName).Distinct().Count());
            Assert.All(saved.Commands, command =>
            {
                var observation = Assert.IsType<CaptureCommandObserved>(command.Observation);
                Assert.Equal("natural-root-exit-and-eof-before-stop", observation.Completion);
                Assert.Equal(0, observation.ExitCode);
                Assert.Contains(command.Root.Process, observation.Stopped.AllFamily);
                Assert.All(observation.Stopped.AllFamily, member =>
                {
                    Assert.Contains(member, saved.Result.HelperStopped!.AllFamily);
                    Assert.Contains(member, saved.Parent!.Family);
                    Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member));
                });
                Assert.Equal(command.Root.Sequence == 2 ? target.ScreenshotTool.Sha256 : target.Tool.Sha256,
                    command.Root.Process.ImageSha256);
            });
            var paired = ReadInvocations(fixture.ScreenshotDirectory!);
            try
            {
                var capture = Assert.Single(paired);
                Assert.Equal(new[] { "-s", AndroidRuntimeFixture.Endpoint, "exec-out", "screencap", "-p" },
                    capture.RootElement.GetProperty("arguments").EnumerateArray().Select(value => value.GetString()));
                Assert.Equal(saved.Commands[1].Root.Process.ProcessId, capture.RootElement.GetProperty("processId").GetInt32());
            }
            finally { foreach (var item in paired) item.Dispose(); }
            var original = File.ReadAllBytes(Path.Combine(fixture.ScreenshotDirectory!, "fixture-screenshot.png"));
            using (var input = new CaptureEvidenceReader(fixture.Source.Instance, fixture.Source.Database, new())
                .OpenCaptured(saved.State.Binding, saved.State.Result!))
            {
                using var bytes = new MemoryStream(); input.CopyTo(bytes); Assert.Equal(original, bytes.ToArray());
            }
            var history = new JournalStore(fixture.Source.Instance, new()).ReadAllRevisions(receipt.JournalId);
            var capacity = JournalCapacityPolicy.Measure(state.Total);
            Assert.True(history.Count <= capacity.Revisions);
            Assert.True(history.Sum(document => document.Export().LongLength) <= capacity.HistoryBytes);
            Assert.InRange(capacity.Jobs, state.Jobs.Length + saved.Commands.Length, 32);
            Assert.True(File.Exists(fixture.Marker + ".post")); fixture.VerifyOriginal();
        });
    }

    [Fact(DisplayName = "Missing Explicit Pairing Produces An Honest Unknown Size Without Starting Any Capture Process")]
    public async Task ReportsMissingPairing()
    {
        var fixture = new AndroidRuntimeFixture(mumu: true, capture: true, forceClose: false, configuration: true);
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = await fixture.RunAsync();
            Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
            var saved = AndroidCaptureTests.ReadCapture(fixture);
            Assert.Equal("unavailable", saved.Result!.Outcome);
            Assert.Equal("execution_capture_tool_unconfigured", saved.Result.ReasonCode);
            Assert.Null(saved.Root); Assert.Empty(saved.Commands); Assert.Null(saved.Result.Png); Assert.Null(saved.Result.Image);
            Assert.Equal(0u, saved.Result.HelperStopped!.TotalProcesses); Assert.Empty(saved.Result.HelperStopped.AllFamily);
            Assert.NotNull(saved.Parent); fixture.VerifyOriginal();
            new ImageCatalogProjection(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Source.Lease).OnCommitted(receipt);
            var image = Assert.Single(new ImageCatalogReader(fixture.Source.Instance, fixture.Source.Database, new())
                .ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId)).Items);
            Assert.Equal(RunImageOutcome.Unavailable, image.Outcome); Assert.Null(image.Width); Assert.Null(image.Height);
        });
    }

    [Theory(DisplayName = "Invalid Or Locally Stopped Paired Output Never Becomes A Captured Image Or A Natural Complete Response")]
    [InlineData("truncated")]
    [InlineData("compressedTail")]
    [InlineData("hang")]
    [InlineData("overflow")]
    [InlineData("offline")]
    public async Task RejectsIncompleteOutput(string mode)
    {
        var fixture = new AndroidRuntimeFixture(mumu: true, pairedCapture: true, capture: true,
            forceClose: false, captureMode: mode);
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = await fixture.RunAsync(); Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
            var saved = AndroidCaptureTests.ReadCapture(fixture);
            Assert.Equal("unavailable", saved.Result!.Outcome); Assert.Null(saved.Result.Image); Assert.Null(saved.Result.Png);
            Assert.All(saved.Result.HelperStopped!.AllFamily,
                member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
            if (mode == "hang")
            {
                var command = Assert.Single(saved.Commands, command => command.Root.Sequence == 2);
                Assert.False(command.Observation?.Completion == "natural-root-exit-and-eof-before-stop");
                Assert.True(File.Exists(Path.Combine(fixture.ScreenshotDirectory!, "fixture-screenshot.png")));
            }
            if (mode == "offline")
            {
                var command = Assert.Single(saved.Commands, command => command.Root.Sequence == 2);
                var observed = Assert.IsType<CaptureCommandObserved>(command.Observation);
                Assert.Equal(1, observed.ExitCode);
                var error = observed.StandardError;
                var expected = "error: device offline"u8.ToArray();
                Assert.Equal((long)expected.Length, error.SizeBytes);
                Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(expected)), error.Sha256);
                using var input = new ImmutableBlobStore(fixture.Source.Instance, new()).OpenVerified(Assert.Single(error.Parts));
                using var captured = new MemoryStream();
                input.CopyTo(captured);
                Assert.Equal(expected, captured.ToArray());
            }
            fixture.VerifyOriginal();
        });
    }

    [Fact(DisplayName = "Capture Quota Failure Keeps The Original Rejection And Its Pipe Fault After Stopping The Actual Command")]
    public async Task PreservesCaptureCommandFailure()
    {
        using var fixture = new AndroidRuntimeFixture(responseAction: "capture", stdout: "over-quota", hang: true, accept: false);
        var image = ExecutionHookImages.Capture(fixture.ToolImage, runtime: true);
        ProcessIdentity? child = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var failure = await Assert.ThrowsAsync<IOException>(() => OwnedCaptureCommand.RunAsync(image,
            ["-s", AndroidRuntimeFixture.Endpoint, "exec-out", "screencap", "-p"], fixture.Source.Instance.InstanceId, Guid.NewGuid(),
            1, 65536, 5000, (_, identity) => child = ProcessIdentityReader.Read(identity.ProcessId), deadline.Token));

        Assert.Equal("execution_capture_output_rejected", failure.Message);
        Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(Assert.IsType<ProcessIdentity>(child)));
        var pipeFailure = Assert.Single(CleanupFailures.Read(failure));
        Assert.Equal("execution_capture_output_rejected", pipeFailure.Message);
        Assert.Contains("OwnedCaptureCommand.ReadAsync", pipeFailure.StackTrace, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "A Server Child Is Collected In The Original Family While An Independent Existing Server Remains Alive")]
    public async Task ScopesServerChildren()
    {
        var fixture = new AndroidRuntimeFixture(mumu: true, pairedCapture: true, capture: true,
            captureMode: "rgb", serverAction: "capture", forceClose: false);
        await fixture.ExecuteAsync(async () =>
        {
            using var external = new OwnedWorkerJob(); external.ObserveProcessIdentities(_ => { });
            var image = Path.Combine(fixture.ScreenshotDirectory!, Path.GetFileName(fixture.ToolImage));
            var root = external.Start(image, ["--fixture-server", fixture.Nonce], fixture.ScreenshotDirectory!,
                fixture.Source.Instance.InstanceId, Guid.NewGuid(), _ => { });
            try
            {
                var receipt = await fixture.RunAsync(); Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
                var saved = AndroidCaptureTests.ReadCapture(fixture);
                Assert.Equal("captured", saved.Result!.Outcome);
                var command = Assert.Single(saved.Commands, command => command.Root.Sequence == 2);
                Assert.True(command.Observation!.Stopped.TotalProcesses >= 2);
                Assert.All(command.Observation.Stopped.AllFamily, member => Assert.Contains(member, saved.Parent!.Family));
                var identity = new ProcessIdentity(root.ProcessId, root.CreatedAtUtc, root.ImagePath, root.ImageSha256, root.UserSid, root.IsElevated);
                Assert.Equal(WriterState.Running, WriterStopEvidence.ObserveProcess(identity));
                Assert.DoesNotContain(identity, saved.Parent!.Family);
            }
            finally { _ = await PreparedStoppedTarget.StopAsync(external, TimeSpan.FromSeconds(15)); }
        });
    }

    internal static JsonDocument[] ReadInvocations(string directory) => Directory.GetFiles(Path.Combine(directory, "observed"), "*.json")
        .Select(path => JsonDocument.Parse(File.ReadAllBytes(path))).ToArray();
}
