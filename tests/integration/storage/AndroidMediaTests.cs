using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Media;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class AndroidMediaTests
{
    [Theory(DisplayName = "Actual Generic And Paired Mu Mu Previews Use Fresh Stopped Children Without Persisting Frames Or Replacing The Final Capture")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturesFreshPreviews(bool mumu)
    {
        var fixture = new AndroidRuntimeFixture(mumu: mumu, pairedCapture: mumu, capture: true,
            captureMode: "rgb", configuration: true, mainWait: true, forceClose: false);
        await fixture.ExecuteAsync(async () =>
        {
            var state = fixture.State;
            var context = new RunMediaContext(fixture.Source.Instance.InstanceId,
                Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId));
            var activation = new ExtensionActivation("live-fixture");
            using var registration = fixture.LiveMedia.Previews.Register(activation, "android", fixture.LiveMedia);
            activation.Publish();
            var running = fixture.Consumer.RunNextAsync();
            try
            {
                await RequireMainAsync(fixture, running);
                await using var preview = await OpenAsync(fixture, context);
                var first = await preview.CaptureAsync();
                var second = await preview.CaptureAsync();
                Assert.Equal(3, first.Width); Assert.Equal(2, first.Height);
                var directory = fixture.ScreenshotDirectory ?? fixture.Directory;
                var original = File.ReadAllBytes(Path.Combine(directory, "fixture-screenshot.png"));
                Assert.Equal(original, first.Png.ToArray()); Assert.Equal(original, second.Png.ToArray());
                var digest = Convert.ToHexStringLower(SHA256.HashData(original));
                Assert.False(File.Exists(Path.Combine(fixture.Source.Instance.RuntimeRoot, "blobs", "content", digest + ".bin")));
                Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Source.Instance.RuntimeRoot, "blobs", "content"), ".capture-*.tmp"));
                File.WriteAllText(fixture.Marker + ".main.release", "release");
                var receipt = Assert.IsType<AttemptReceipt>(await running.WaitAsync(TimeSpan.FromSeconds(90)));
                Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
                var saved = AndroidCaptureTests.ReadCapture(fixture);
                Assert.Equal("captured", saved.Result!.Outcome);
                Assert.Equal(digest, saved.Result.Png!.Sha256);
                Assert.All(saved.Parent!.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
                Assert.Equal(mumu ? 3 : 0, saved.Commands.Length);
                var captured = ReadCaptures(directory);
                Assert.Equal(3, captured);
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => preview.CaptureAsync().AsTask());
                fixture.VerifyOriginal(); Assert.Equal(0, fixture.Consumer.PendingReconciliationCount);
            }
            finally
            {
                File.WriteAllText(fixture.Marker + ".main.release", "release");
                await running.WaitAsync(TimeSpan.FromSeconds(90));
            }
        });
    }

    private static async Task RequireMainAsync(AndroidRuntimeFixture fixture, Task<AttemptReceipt?> running)
    {
        var clock = Stopwatch.StartNew();
        while (!File.Exists(fixture.Marker + ".main"))
        {
            if (running.IsCompleted)
            {
                var receipt = await running;
                throw new IOException("live_fixture_completed_before_main:" + JsonSerializer.Serialize(receipt?.Facts));
            }
            if (clock.Elapsed > TimeSpan.FromSeconds(90)) throw new TimeoutException("live_fixture_main_not_ready");
            await Task.Delay(10);
        }
    }

    private static int ReadCaptures(string directory)
    {
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(Path.Combine(directory, "observed"), "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.GetProperty("arguments").EnumerateArray().Any(item => item.GetString() == "exec-out")) count++;
        }
        return count;
    }

    private static async Task<ExecutionLivePreview> OpenAsync(AndroidRuntimeFixture fixture, RunMediaContext context)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try { return fixture.LiveMedia.OpenPreview(AndroidRuntimeFixture.Subject, context, "live-fixture", "android"); }
            catch (UnauthorizedAccessException) when (clock.Elapsed < TimeSpan.FromSeconds(10)) { await Task.Delay(20); }
        }
    }
}
