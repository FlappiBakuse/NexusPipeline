using System.Diagnostics;
using System.Text.Json;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class DesktopStartTests
{
    [Fact(DisplayName = "A Desktop Root Can Be Bound While Suspended And Executes Only After Backend Publication")]
    public async Task StartsAfterPublication()
    {
        var (directory, fixture, marker) = Inputs();
        using var job = new OwnedWorkerJob();
        using var release = new ManualResetEventSlim();
        var registered = new TaskCompletionSource<OwnedWorkerIdentity>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launch = Task.Run(() => job.StartDesktop(fixture, [marker, "backend-ready"], directory, Guid.NewGuid(), Guid.NewGuid(), identity =>
        { registered.TrySetResult(identity); release.Wait(); }));
        try
        {
            var identity = await registered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using (var binding = new NativeClientBinding(identity.ProcessId)) Assert.True(binding.IsLive);
            await Task.Delay(200);
            Assert.False(File.Exists(marker));
            release.Set();
            Assert.Equal(identity, await launch.WaitAsync(TimeSpan.FromSeconds(15)));
            await AwaitMarker(marker);
            using var document = JsonDocument.Parse(File.ReadAllBytes(marker));
            Assert.Equal(identity.ProcessId, document.RootElement.GetProperty("processId").GetInt32());
            Assert.Equal("backend-ready", document.RootElement.GetProperty("nonce").GetString());
        }
        finally
        {
            release.Set();
            await launch.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
        }
    }

    [Fact(DisplayName = "Cancelled Backend Startup Stops Its Registered Suspended Desktop Without Executing It")]
    public async Task CancelsBeforePublication()
    {
        var (directory, fixture, marker) = Inputs();
        using var job = new OwnedWorkerJob();
        using var release = new ManualResetEventSlim();
        using var stop = new CancellationTokenSource();
        var registered = new TaskCompletionSource<OwnedWorkerIdentity>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launch = Task.Run(() => job.StartDesktop(fixture, [marker, "cancelled-backend"], directory, Guid.NewGuid(), Guid.NewGuid(), identity =>
        { registered.TrySetResult(identity); release.Wait(stop.Token); }));
        try
        {
            var identity = await registered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var process = Process.GetProcessById(identity.ProcessId);
            _ = process.Handle;
            Assert.False(File.Exists(marker));
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await launch);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(1, process.ExitCode);
            Assert.False(File.Exists(marker));
        }
        finally
        {
            stop.Cancel();
            try { await launch.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (OperationCanceledException) { }
            Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
        }
    }

    private static (string Directory, string Fixture, string Marker) Inputs()
    {
        var root = Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new InvalidOperationException();
        var fixture = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE") ?? throw new InvalidOperationException();
        var directory = Path.Combine(root, "desktop-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return (directory, fixture, Path.Combine(directory, "ready.json"));
    }

    [Fact(DisplayName = "Desktop Roots Do Not Inherit Another Application's Crash Reporter Or Dump Location")]
    public async Task SanitizesCrashReporter()
    {
        var (directory, fixture, marker) = Inputs();
        var names = new[] { "CHROME_CRASHPAD_PIPE_NAME", "CHROME_CRASHPAD_SERVER_URL", "BREAKPAD_DUMP_LOCATION", "ELECTRON_RUN_AS_NODE" };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        using var job = new OwnedWorkerJob();
        try
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, "unrelated-parent-" + name);
            _ = job.StartDesktop(fixture, ["desktop-environment", marker], directory, Guid.NewGuid(), Guid.NewGuid());
            await AwaitMarker(marker);
            using var document = JsonDocument.Parse(File.ReadAllBytes(marker));
            Assert.Empty(document.RootElement.GetProperty("present").EnumerateArray());
            Assert.All(names, name => Assert.Equal("unrelated-parent-" + name, Environment.GetEnvironmentVariable(name)));
        }
        finally
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, previous[name]);
            Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
        }
    }

    private static async Task AwaitMarker(string path)
    {
        var watch = Stopwatch.StartNew();
        while (!File.Exists(path) && watch.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(20);
        Assert.True(File.Exists(path));
    }
}
