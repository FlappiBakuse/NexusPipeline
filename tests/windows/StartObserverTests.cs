using NexusPipeline.Platform.Processes;
using System.Text.Json;
using System.Diagnostics;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class StartObserverTests
{
    [Fact(DisplayName = "Inactive Job Observer Reports Owned Root And Helpers And Detaches Before Later Starts")]
    public async Task TracksOwnedStarts()
    {
        var (root, fixture) = Inputs();
        using var job = new OwnedWorkerJob();
        var observed = new TaskCompletionSource<ProcessIdentity>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var observer = job.ObserveProcessStarts(pid =>
        {
            var identity = ProcessIdentityReader.Read(pid);
            Interlocked.Increment(ref count);
            observed.TrySetResult(identity);
        });
        Assert.Throws<InvalidOperationException>(() => job.ObserveProcessStarts(_ => { }));
        var firstReady = Path.Combine(root, Guid.NewGuid() + ".json");
        var secondReady = Path.Combine(root, Guid.NewGuid() + ".json");
        var first = job.Start(fixture, [firstReady, "observer"], root,
            Guid.NewGuid(), Guid.NewGuid(), _ => { });
        var actual = await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(first.ProcessId, actual.ProcessId);
        Assert.Equal(first.CreatedAtUtc, actual.CreatedAtUtc);
        Assert.Equal(first.ImageSha256, actual.ImageSha256);
        using var retained = Process.GetProcessById(first.ProcessId);
        using var caller = Process.GetCurrentProcess();
        Assert.True(job.ContainsProcessHandle(retained.SafeHandle));
        Assert.False(job.ContainsProcessHandle(caller.SafeHandle));
        await AwaitFileAsync(firstReady);
        Assert.True(observer.Drain(TimeSpan.FromSeconds(5)));
        var beforeDetach = job.GetAccounting();
        observer.Dispose();
        var notificationsAtDetach = Volatile.Read(ref count);
        var second = job.Start(fixture, [secondReady, "detached"], root,
            Guid.NewGuid(), Guid.NewGuid(), _ => { });
        await AwaitFileAsync(secondReady);
        var active = job.ActiveProcessIds().Select(pid => ProcessIdentityReader.Read(pid)).ToArray();
        File.WriteAllText(Path.Combine(root, "job-observer-boundary.json"), JsonSerializer.Serialize(new
        {
            first, second, beforeDetach, afterSecondStart = job.GetAccounting(), active, notifications = Volatile.Read(ref count), observer.Failures
        }));
        Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal((uint)active.Length, job.GetAccounting().TotalProcesses);
        Assert.Contains(active, identity => identity.ProcessId == first.ProcessId);
        Assert.Contains(active, identity => identity.ProcessId == second.ProcessId);
        Assert.Equal((uint)notificationsAtDetach, beforeDetach.TotalProcesses);
        Assert.Equal(notificationsAtDetach, Volatile.Read(ref count));
        Assert.Empty(observer.Failures);
    }

    [Fact(DisplayName = "Observer Failure Is Reported And Cannot Hide Or Stop The Owned Worker")]
    public async Task PreservesObserverFailure()
    {
        var (root, fixture) = Inputs();
        using var job = new OwnedWorkerJob();
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var observer = job.ObserveProcessStarts(_ =>
        {
            called.SetResult();
            throw new IOException("Controlled observation failure");
        });
        var started = job.Start(fixture, [Path.Combine(root, Guid.NewGuid() + ".json"), "failed-observer"], root,
            Guid.NewGuid(), Guid.NewGuid(), _ => { });
        await called.Task.WaitAsync(TimeSpan.FromSeconds(10));
        observer.Dispose();
        Assert.Contains(observer.Failures, failure => failure.EndsWith(":IOException", StringComparison.Ordinal));
        Assert.Contains(started.ProcessId, job.ActiveProcessIds());
        Assert.Throws<InvalidOperationException>(() => job.ObserveProcessStarts(_ => { }));
        Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
    }

    private static (string Root, string Fixture) Inputs()
    {
        var root = Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new IOException("Missing external test root");
        var fixture = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE") ?? throw new IOException("Missing built fixture");
        return (root, fixture);
    }

    private static async Task AwaitFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(path)) await Task.Delay(10, timeout.Token);
    }
}
