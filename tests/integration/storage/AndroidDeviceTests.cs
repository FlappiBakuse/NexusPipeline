using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class AndroidDeviceTests
{
    [Theory(DisplayName = "The Authenticated Android Worker Captures Original Bytes And Uses Frozen Endpoint Commands Inside Its Original Process Family")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UsesFrozenDevice(bool mumu)
    {
        await using var fixture = await ProviderAndroidFixture.CreateAsync(false, mumu);
        var runtime = fixture.Runtime;
        var receipt = await runtime.RunAsync().WaitAsync(TimeSpan.FromSeconds(90));
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
        var error = runtime.Marker + ".device-error";
        Assert.True(receipt.Facts.Engine == EngineOutcome.Succeeded, File.Exists(error) ? File.ReadAllText(error) : receipt.Facts.Engine.ToString());
        Assert.Equal(File.ReadAllBytes(fixture.ScreenshotPath), File.ReadAllBytes(runtime.Marker + ".device-frame.png"));
        var invocations = fixture.Invocations();
        var arguments = invocations.Select(value => value.GetProperty("arguments").EnumerateArray().Select(item => item.GetString()!).ToArray()).ToArray();
        Assert.All(arguments, value => Assert.True(mumu ? value.Length >= 3 && value[1] == "-v" && value[2] is "0" or "all" :
            value.SequenceEqual(new[] { "connect", "127.0.0.1:16384" }) ||
            value.Length >= 3 && value[0] == "-s" && value[1] == "127.0.0.1:16384"));
        Assert.Contains(arguments, value => value[^1] == "input tap 123 45");
        Assert.Contains(arguments, value => value[^1] == "input text 'nexus%sdevice'");
        Assert.DoesNotContain(arguments, value => value.Any(item => item.Contains("com.foreign.app", StringComparison.Ordinal)));
        var family = RequireStopped(runtime);
        Assert.All(invocations.Where(value => value.GetProperty("arguments").EnumerateArray().Any(item => item.GetString()!.StartsWith("input ", StringComparison.Ordinal))),
            value => Assert.Contains(value.GetProperty("processId").GetInt32(), family));
    }

    [Theory(DisplayName = "Cancellation Stops An Inflight Android Device Command And Its Spawned Server Before Releasing The Original Worker")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelsOwnedCommand(bool mumu)
    {
        await using var fixture = await ProviderAndroidFixture.CreateAsync(true, mumu);
        var runtime = fixture.Runtime;
        var running = runtime.RunAsync();
        try
        {
            var marker = Path.Combine(fixture.ToolDirectory, "server-ready");
            var timer = Stopwatch.StartNew();
            while (!File.Exists(marker) && !running.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(40)) await Task.Delay(20);
            var error = runtime.Marker + ".device-error";
            Assert.True(File.Exists(marker), File.Exists(error) ? File.ReadAllText(error) : "android_device_server_not_started");
            var serverPid = int.Parse(File.ReadAllText(marker), System.Globalization.CultureInfo.InvariantCulture);
            runtime.Cancel();
            var receipt = await running.WaitAsync(TimeSpan.FromSeconds(40));
            Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
            Assert.Contains(serverPid, RequireStopped(runtime));
        }
        finally { if (!running.IsCompleted) { runtime.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(40)); } }
    }

    private static int[] RequireStopped(ProviderRuntimeFixture runtime)
    {
        var state = runtime.State; var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        var closure = new JobClosureStore(runtime.Source.Instance, runtime.Source.Database, new()).ReadForExecutionEvidence(
            result.Binding.ParentJournalId, result.Binding.WriterId, result.Binding.JobName, result.Binding.SessionId);
        Assert.NotNull(closure);
        Assert.All(closure.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        Assert.Equal(0, runtime.Base.Count("resource_isolations"));
        return closure.Family.Select(value => value.ProcessId).ToArray();
    }

    [Theory(DisplayName = "Android Device Command Timeout Output Overflow And A Changed MuMu Mapping Reject Without Replaying Effects And Close The Native Family")]
    [InlineData("timeout", false)]
    [InlineData("overflow", false)]
    [InlineData("mapping", true)]
    public async Task LimitsOwnedCommands(string fault, bool mumu)
    {
        await using var fixture = await ProviderAndroidFixture.CreateAsync(false, mumu, fault);
        var runtime = fixture.Runtime;
        var receipt = await runtime.RunAsync().WaitAsync(TimeSpan.FromSeconds(60));
        var error = runtime.Marker + ".device-error";
        Assert.True(receipt.Facts.Engine == EngineOutcome.Succeeded, File.Exists(error) ? File.ReadAllText(error) : receipt.Facts.Engine.ToString());
        var elapsed = long.Parse(File.ReadAllText(runtime.Marker + ".device-denied"), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(elapsed, 0, 10000);
        var family = RequireStopped(runtime);
        var invocations = fixture.Invocations();
        if (fault == "mapping")
            Assert.All(invocations, value => Assert.Equal("info", value.GetProperty("arguments")[0].GetString()));
        else
        {
            var invoked = Assert.Single(invocations, value => value.GetProperty("arguments").EnumerateArray()
                .Any(item => item.GetString()!.StartsWith("device-fixture-", StringComparison.Ordinal)));
            Assert.Contains(invoked.GetProperty("processId").GetInt32(), family);
            if (fault == "timeout")
                Assert.Contains(int.Parse(File.ReadAllText(Path.Combine(fixture.ToolDirectory, "server-ready")), System.Globalization.CultureInfo.InvariantCulture), family);
        }
    }
}
