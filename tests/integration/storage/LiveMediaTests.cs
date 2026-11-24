using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Media;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class LiveMediaTests
{
    [Theory(DisplayName = "Running Window Previews Create No Png Files And Close Before The Original Final Capture And Restore")]
    [InlineData(1)]
    [InlineData(3)]
    public async Task ScopesRunningPreviews(int frames)
    {
        var fixture = new PcStageFixture(configuration: true, distinctConfiguration: true,
            hooks: false, mainWait: true, nativeWindow: true, captureFrames: frames);
        await fixture.ExecuteAsync(async () =>
        {
            var state = fixture.State;
            var context = new RunMediaContext(fixture.Source.Instance.InstanceId,
                Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId));
            var activation = new ExtensionActivation("live-fixture");
            using var registration = fixture.LiveMedia.Previews.Register(activation, "window", fixture.LiveMedia);
            activation.Publish();
            Assert.Throws<UnauthorizedAccessException>(() => fixture.LiveMedia.OpenPreview(
                PcStageFixture.Subject, context, "live-fixture", "window"));
            var original = File.ReadAllBytes(fixture.Source.Configuration);
            var running = fixture.Consumer.RunNextAsync();
            try
            {
                await fixture.AwaitMainAsync();
                await using var preview = await OpenAsync(fixture, context);
                Assert.Throws<UnauthorizedAccessException>(() => fixture.LiveMedia.OpenPreview("caller-supplied-other-subject", context, "live-fixture", "window"));
                Assert.Throws<UnauthorizedAccessException>(() => fixture.LiveMedia.OpenPreview(
                    PcStageFixture.Subject, new(context.InstanceId, context.RunId, Guid.NewGuid()), "live-fixture", "window"));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.LiveMedia.CaptureAsync(
                    new(context, Guid.NewGuid(), Guid.NewGuid(), 1, "live-fixture"), default).AsTask());
                var otherActivation = new ExtensionActivation("other-fixture");
                using var otherRegistration = fixture.LiveMedia.Previews.Register(otherActivation, "window", fixture.LiveMedia);
                otherActivation.Publish();
                await using var otherPreview = fixture.LiveMedia.OpenPreview(PcStageFixture.Subject, context, "other-fixture", "window");
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.LiveMedia.CaptureAsync(
                    new(context, preview.PreviewId, Guid.NewGuid(), 1, "other-fixture"), default).AsTask());
                using var writes = new PreviewWriteWatch(fixture.Source.Instance.RuntimeRoot);
                DateTimeOffset? captured = null;
                for (var frame = 0; frame < 4; frame++)
                {
                    var image = await preview.CaptureAsync();
                    Assert.True(image.Width > 0 && image.Height is > 0 and <= 360);
                    Assert.True(image.SizeBytes >= 33 && (captured is null || image.CapturedAt >= captured));
                    captured = image.CapturedAt;
                }
                await writes.RequireNoPngAsync();
                Assert.False(running.IsCompleted);
                await preview.DisposeAsync();
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => preview.CaptureAsync().AsTask());
                File.WriteAllText(fixture.HookMarker + ".main.release", "release");
                var receipt = Assert.IsType<AttemptReceipt>(await running.WaitAsync(TimeSpan.FromSeconds(45)));
                Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
                Assert.Equal(original, File.ReadAllBytes(fixture.Source.Configuration));
                Assert.Throws<UnauthorizedAccessException>(() => fixture.LiveMedia.OpenPreview(
                    PcStageFixture.Subject, context, "live-fixture", "window"));
                Assert.Equal(0, fixture.Consumer.PendingReconciliationCount);
            }
            finally
            {
                File.WriteAllText(fixture.HookMarker + ".main.release", "release");
                await running.WaitAsync(TimeSpan.FromSeconds(45));
            }
        });
    }

    [Fact(DisplayName = "Preview Buffers Reject Excess And Truncated Input And Honor Cancellation")]
    public async Task RejectsInvalidPreviewInput()
    {
        using var oversized = new NexusPipeline.Platform.Media.CapturePngSpool(Guid.NewGuid());
        using var large = new MemoryStream(new byte[NexusPipeline.Platform.Media.CapturePngSpool.MaximumBytes + 1]);
        var limit = await Assert.ThrowsAsync<IOException>(() => oversized.ReadOutputAsync(large, default));
        Assert.Equal("capture_worker_frame_byte_limit", limit.Message);

        using var truncated = new NexusPipeline.Platform.Media.CapturePngSpool(Guid.NewGuid());
        using var shortInput = new MemoryStream(new byte[10]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => truncated.ReadLengthAsync(shortInput, 57, default));
        Assert.Throws<IOException>(() => truncated.ReadLiveFrame());

        using var canceled = new NexusPipeline.Platform.Media.CapturePngSpool(Guid.NewGuid());
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.ReadOutputAsync(large, stop.Token));
    }

    [Theory(DisplayName = "Preview Output Preserves Aspect Ratio Capture Time And Original Bytes")]
    [InlineData(1280, 720, 640, 360)]
    [InlineData(720, 1280, 202, 360)]
    [InlineData(320, 240, 320, 240)]
    public async Task ScalesPreviewFrames(int width, int height, int expectedWidth, int expectedHeight)
    {
        using var png = new MemoryStream();
        NexusPipeline.Platform.Media.PngScanlineEncoder.Write(png, width, height, (_, row) =>
        {
            for (var x = 0; x < width; x++) { row[x * 4] = 37; row[x * 4 + 1] = 71; row[x * 4 + 2] = 149; row[x * 4 + 3] = 255; }
        });
        var original = png.ToArray();
        var capturedAt = new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
        var frame = new PreviewImage(width, height, original, capturedAt);
        var preview = await NexusPipeline.Platform.Media.PreviewScaler.ScaleAsync(frame, default);
        Assert.Equal(expectedWidth, preview.Width); Assert.Equal(expectedHeight, preview.Height);
        Assert.Equal(capturedAt, preview.CapturedAt); Assert.Equal(original, frame.Png.ToArray());
        using var actual = new MemoryStream(preview.Png.ToArray());
        var info = NexusPipeline.Platform.Media.BoundedCapturePng.Verify(actual, allowRgba: false);
        Assert.Equal(expectedWidth, info.Width); Assert.Equal(expectedHeight, info.Height);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NexusPipeline.Platform.Media.PreviewScaler.ScaleAsync(frame, canceled.Token).AsTask());
    }

    [Fact(DisplayName = "An Unrelated Step Can Open Authorize And Close While A Live Callback Is Blocked")]
    public async Task AuthorizesDuringAnotherStepsLivenessCheck()
    {
        var instance = Guid.NewGuid();
        var media = new ExecutionLiveMedia(instance);
        var first = new RunMediaContext(instance, Guid.NewGuid(), Guid.NewGuid());
        var other = new RunMediaContext(instance, Guid.NewGuid(), Guid.NewGuid());
        using var gate = new LivenessGate();
        await using var step = media.Register("subject", first, gate.Require, NoCapture);
        await using var otherStep = media.Register("subject", other, () => { }, NoCapture);
        var extension = new ExtensionActivation("live-authority-fixture");
        using var provider = media.Previews.Register(extension, "window", media);
        extension.Publish();
        await using var preview = media.OpenPreview("subject", first, extension.ExtensionId, "window");
        gate.Block = true;
        var checking = Task.Run(() => media.AssertAllowed(first, extension.ExtensionId));
        Task? unrelated = null;
        try
        {
            await gate.Entered.Task.WaitAsync(FixtureTimeout);
            unrelated = Task.Run(async () =>
            {
                await using var independent = media.OpenPreview("subject", other, extension.ExtensionId, "window");
                media.AssertAllowed(other, extension.ExtensionId);
            });
            await unrelated.WaitAsync(FixtureTimeout);
            Assert.False(checking.IsCompleted);
            Assert.Throws<UnauthorizedAccessException>(() => media.AssertAllowed(other, extension.ExtensionId));
        }
        finally
        {
            gate.Release();
            await checking.WaitAsync(FixtureTimeout);
            if (unrelated is not null) await unrelated.WaitAsync(FixtureTimeout);
        }
    }

    [Theory(DisplayName = "Closing Revokes A Blocked Grant Step Or Pending Open And Waits For Its Authority Borrow")]
    [InlineData("grant")]
    [InlineData("step")]
    [InlineData("opening")]
    public async Task RevokesInFlightLiveness(string target)
    {
        var instance = Guid.NewGuid();
        var context = new RunMediaContext(instance, Guid.NewGuid(), Guid.NewGuid());
        var media = new ExecutionLiveMedia(instance);
        using var gate = new LivenessGate();
        await using var step = media.Register("subject", context, gate.Require, NoCapture);
        var extension = new ExtensionActivation("live-revocation-fixture");
        using var provider = media.Previews.Register(extension, "window", media);
        extension.Publish();
        var opening = target == "opening";
        await using var preview = opening ? null : media.OpenPreview("subject", context, extension.ExtensionId, "window");
        gate.Block = true;
        var checking = Task.Run(async () =>
        {
            if (opening)
            {
                await using var unexpectedlyOpened = media.OpenPreview("subject", context, extension.ExtensionId, "window");
            }
            else await preview!.CaptureAsync();
        });
        var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? close = null;
        try
        {
            await gate.Entered.Task.WaitAsync(FixtureTimeout);
            if (opening)
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() =>
                    media.Previews.Open(context, extension.ExtensionId, "window").Dispose()).WaitAsync(FixtureTimeout));
            close = Task.Run(async () =>
            {
                var revoked = target == "grant" ? preview!.DisposeAsync() : step.DisposeAsync();
                closing.TrySetResult();
                await revoked;
            });
            await closing.Task.WaitAsync(FixtureTimeout);
            Assert.False(close.IsCompleted);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                Task.Run(() => media.AssertAllowed(context, extension.ExtensionId)).WaitAsync(FixtureTimeout));
        }
        finally
        {
            gate.Release();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => checking.WaitAsync(FixtureTimeout));
            if (close is not null) await close.WaitAsync(FixtureTimeout);
        }
    }

    [Fact(DisplayName = "Closing One Grant Does Not Revoke Another Grant For The Same Live Step And Extension")]
    public async Task RetainsSiblingAuthorization()
    {
        var instance = Guid.NewGuid();
        var context = new RunMediaContext(instance, Guid.NewGuid(), Guid.NewGuid());
        var media = new ExecutionLiveMedia(instance);
        using var gate = new LivenessGate();
        await using var step = media.Register("subject", context, gate.Require, NoCapture);
        var extension = new ExtensionActivation("live-sibling-fixture");
        using var provider = media.Previews.Register(extension, "window", media);
        extension.Publish();
        await using var first = media.OpenPreview("subject", context, extension.ExtensionId, "window");
        await using var second = media.OpenPreview("subject", context, extension.ExtensionId, "window");
        gate.Block = true;
        var checking = Task.Run(() => media.AssertAllowed(context, extension.ExtensionId));
        Task? close = null;
        try
        {
            await gate.Entered.Task.WaitAsync(FixtureTimeout);
            close = first.DisposeAsync().AsTask();
            await close.WaitAsync(FixtureTimeout);
            Assert.False(checking.IsCompleted);
        }
        finally
        {
            gate.Release();
            await checking.WaitAsync(FixtureTimeout);
            if (close is not null) await close.WaitAsync(FixtureTimeout);
        }
        media.AssertAllowed(context, extension.ExtensionId);
    }

    [Theory(DisplayName = "Revocation Discards A Returning Frame And A Grant Cannot Replay Its Sequence")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscardsFramesAfterClosure(bool closeStep)
    {
        var instance = Guid.NewGuid();
        var context = new RunMediaContext(instance, Guid.NewGuid(), Guid.NewGuid());
        var media = new ExecutionLiveMedia(instance);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var image = new PreviewImage(1, 1, new byte[33]);
        var calls = 0;
        await using var step = media.Register("subject", context, () => { }, async _ =>
        {
            if (Interlocked.Increment(ref calls) > 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(FixtureTimeout);
            }
            return image;
        });
        var extension = new ExtensionActivation("live-frame-fixture");
        using var provider = media.Previews.Register(extension, "window", media);
        extension.Publish();
        await using var preview = media.OpenPreview("subject", context, extension.ExtensionId, "window");
        var request = new MediaCaptureRequest(context, preview.PreviewId, Guid.NewGuid(), 1, extension.ExtensionId);
        Assert.Same(image, await media.CaptureAsync(request, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => media.CaptureAsync(request, default).AsTask());
        var next = request with { RequestId = Guid.NewGuid(), Sequence = 2 };
        var pending = media.CaptureAsync(next, default).AsTask();
        Task? close = null;
        try
        {
            await entered.Task.WaitAsync(FixtureTimeout);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => media.CaptureAsync(next, default).AsTask());
            close = (closeStep ? step.DisposeAsync() : preview.DisposeAsync()).AsTask();
            Assert.False(close.IsCompleted);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                media.CaptureAsync(next with { Sequence = 3 }, default).AsTask());
        }
        finally
        {
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(FixtureTimeout));
            if (close is not null) await close.WaitAsync(FixtureTimeout);
        }
        Assert.Equal(2, calls);
    }

    private static readonly TimeSpan FixtureTimeout = TimeSpan.FromSeconds(10);
    private static ValueTask<PreviewImage> NoCapture(CancellationToken _) => throw new InvalidOperationException("fixture_no_capture_expected");

    private sealed class LivenessGate : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        internal volatile bool Block;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Require()
        {
            if (!Block) return;
            Entered.TrySetResult();
            if (!_release.Wait(FixtureTimeout + FixtureTimeout)) throw new TimeoutException("fixture_liveness_release");
        }

        internal void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }

    private static async Task<ExecutionLivePreview> OpenAsync(
        PcStageFixture fixture, RunMediaContext context)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try { return fixture.LiveMedia.OpenPreview(PcStageFixture.Subject, context, "live-fixture", "window"); }
            catch (UnauthorizedAccessException) when (clock.Elapsed < TimeSpan.FromSeconds(10)) { await Task.Delay(20); }
        }
    }
}
