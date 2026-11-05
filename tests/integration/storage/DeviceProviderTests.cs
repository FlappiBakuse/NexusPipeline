using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class DeviceProviderTests
{
    [Theory(DisplayName = "The Actual Authenticated Worker Captures Its Original Full Size Window And Uses Only Bound Inputs With All Pressed Keys Released At Completion Or Cancellation")]
    [InlineData(false, "device-inputs")]
    [InlineData(true, "device-cancel")]
    [InlineData(false, "device-gdi")]
    [InlineData(false, "device-screenDc")]
    [InlineData(false, "device-postMessage")]
    [InlineData(false, "device-framePool")]
    public async Task UsesBoundTarget(bool cancel, string mode)
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync(mode);
        var running = fixture.RunAsync();
        if (mode == "device-seize") await DeviceForegroundFixture.FocusAsync(fixture, running);
        await WaitAsync(fixture.Marker + ".device-ready", running);
        if (cancel) fixture.Cancel();
        var receipt = await running.WaitAsync(TimeSpan.FromSeconds(45));
        Assert.Equal(cancel ? LifecycleOutcome.Cancelled : LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
        if (!cancel) Assert.Equal(EngineOutcome.Succeeded, receipt.Facts.Engine);
        Assert.Equal(64, File.ReadAllText(fixture.Marker + ".device-ready").Length);
        var input = Inputs(fixture);
        Assert.Contains(input, item => item.GetProperty("message").GetInt32() == 0x201 && item.GetProperty("parameter").GetInt64() == (123 | 45 << 16));
        Assert.Contains(input, item => item.GetProperty("message").GetInt32() == 0x100 && item.GetProperty("value").GetInt32() == 16);
        Assert.Contains(input, item => item.GetProperty("message").GetInt32() == 0x101 && item.GetProperty("value").GetInt32() == 16);
        Assert.Equal(mode is "device-inputs" or "device-cancel" or "device-cursor" ? "nexus" : mode == "device-seize" ? "Anexus" : "anexus", string.Concat(input.Where(item => item.GetProperty("message").GetInt32() == 0x102)
            .Select(item => (char)item.GetProperty("value").GetInt32())));
        Assert.Contains(input, item => item.GetProperty("message").GetInt32() == 0x20a);
        Assert.Contains(input, item => item.GetProperty("message").GetInt32() == 0x200 && item.GetProperty("parameter").GetInt64() == (50 | 60 << 16));
        AssertStopped(fixture);
    }

    [Theory(DisplayName = "Foreground And Cursor Positioned Inputs Reach The Frozen Window And Release Their Owned State")]
    [InlineData("device-seize")]
    [InlineData("device-cursor")]
    public Task UsesDesktopInput(string mode) => UsesBoundTarget(false, mode);

    [Theory(DisplayName = "Wrong Device Request Fields Sequences And An Actual Worker Child Cannot Acquire The Original Root Target Permission")]
    [InlineData("arguments")]
    [InlineData("sequence")]
    [InlineData("peer")]
    [InlineData("frozen")]
    public async Task RejectsForeignDevice(string reason)
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("device-" + reason);
        var receipt = await fixture.RunAsync().WaitAsync(TimeSpan.FromSeconds(45));
        Assert.Equal(EngineOutcome.Succeeded, receipt.Facts.Engine);
        if (reason == "peer") Assert.Equal("denied", File.ReadAllText(fixture.Marker + ".device-peer"));
        else Assert.Equal(reason, File.ReadAllText(fixture.Marker + ".device-denied"));
        Assert.DoesNotContain(Inputs(fixture), item => item.GetProperty("message").GetInt32() is 0x100 or 0x101 or 0x102 or 0x201 or 0x202);
        AssertStopped(fixture);
    }

    private static JsonElement[] Inputs(ProviderRuntimeFixture fixture)
    {
        var path = fixture.Marker + ".window.input";
        if (!File.Exists(path)) return [];
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(input); var text = reader.ReadToEnd(); var last = text.LastIndexOf('\n');
        return last < 0 ? [] : text[..last].Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
    }

    [Theory(DisplayName = "Cancellation Closes An Actual Inflight Device Gesture Before Releasing Its Owned Mouse Button And Modifier Instead Of Waiting For The Sixty Second Swipe")]
    [InlineData("device-drag")]
    [InlineData("device-drag-cursor")]
    public async Task CancelsActiveGesture(string mode)
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync(mode);
        var running = fixture.RunAsync(); var timer = Stopwatch.StartNew();
        try
        {
            if (mode == "device-drag-seize") await DeviceForegroundFixture.FocusAsync(fixture, running);
            while (!running.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(25) &&
                !Inputs(fixture).Any(item => item.GetProperty("message").GetInt32() == 0x201 && item.GetProperty("parameter").GetInt64() == (80 | 90 << 16)))
                await Task.Delay(20);
            Assert.False(running.IsCompleted);
            Assert.Contains(Inputs(fixture), item => item.GetProperty("message").GetInt32() == 0x201 && item.GetProperty("parameter").GetInt64() == (80 | 90 << 16));
            fixture.Cancel();
            var receipt = await running.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
            var inputs = Inputs(fixture);
            Assert.Contains(inputs, item => item.GetProperty("message").GetInt32() == 0x202 && item.GetProperty("parameter").GetInt64() >= (80 | 90 << 16));
            Assert.Contains(inputs, item => item.GetProperty("message").GetInt32() == 0x101 && item.GetProperty("value").GetInt32() == 16);
            AssertStopped(fixture);
        }
        finally { if (!running.IsCompleted) { fixture.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(30)); } }
    }
    [Fact(DisplayName = "Cancellation Releases The Actual Foreground Mouse Button And Modifier During A Native Drag")]
    public Task CancelsDesktopGesture() => CancelsActiveGesture("device-drag-seize");
    private static async Task WaitAsync(string path, Task running)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(path) && !running.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(25)) await Task.Delay(20);
        var errorPath = path.Replace(".device-ready", ".device-error", StringComparison.Ordinal);
        Assert.True(File.Exists(path), File.Exists(errorPath) ? File.ReadAllText(errorPath) : "actual_device_worker_not_ready");
    }
    private static void AssertStopped(ProviderRuntimeFixture fixture)
    {
        var state = fixture.State; var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        var closure = new JobClosureStore(fixture.Source.Instance, fixture.Source.Database, new()).ReadForExecutionEvidence(
            result.Binding.ParentJournalId, result.Binding.WriterId, result.Binding.JobName, result.Binding.SessionId);
        Assert.NotNull(closure);
        Assert.All(closure.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        Assert.Equal(0, fixture.Base.Count("resource_isolations"));
    }
}
