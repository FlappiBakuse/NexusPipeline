using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Media;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class LiveMediaTests
{
    [Fact(DisplayName = "Actual Running Window Previews Require The Server Subject And Close Before The Original Final Capture And Restore")]
    public async Task ScopesRunningPreviews()
    {
        var fixture = new PcStageFixture(configuration: true, distinctConfiguration: true,
            hooks: false, mainWait: true, nativeWindow: true);
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
                var first = await preview.CaptureAsync();
                var second = await preview.CaptureAsync();
                Assert.True(first.Width > 0 && first.Height is > 0 and <= 360);
                Assert.True(second.CapturedAt >= first.CapturedAt);
                Assert.True(second.SizeBytes >= 33);
                Assert.False(running.IsCompleted);
                Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Source.Instance.RuntimeRoot, "blobs", "content"), ".capture-*.tmp"));
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

    private static async Task<NexusPipeline.Platform.Storage.Execution.ExecutionLivePreview> OpenAsync(
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
