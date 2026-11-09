using System.Collections.Concurrent;
using System.Diagnostics;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class StartObserverTests
{
    [Fact(DisplayName = "Job Observer Reports Owned Processes And Detaches Before Later Starts")]
    public async Task TracksOwnedStarts()
    {
        var (root, fixture) = Inputs();
        using var job = new OwnedWorkerJob();
        var observed = new ConcurrentQueue<ProcessIdentity>();
        using var observer = job.ObserveProcessStarts(pid => observed.Enqueue(ProcessIdentityReader.Read(pid)));
        Assert.Throws<InvalidOperationException>(() => job.ObserveProcessStarts(_ => { }));
        var firstReady = Path.Combine(root, Guid.NewGuid() + ".json");
        var secondReady = Path.Combine(root, Guid.NewGuid() + ".json");
        var first = job.Start(fixture, [firstReady, "observer"], root,
            Guid.NewGuid(), Guid.NewGuid(), _ => { });
        await AwaitFileAsync(firstReady);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!observed.Any(identity => identity.ProcessId == first.ProcessId)) await Task.Delay(10, deadline.Token);
        Assert.True(observer.Drain(TimeSpan.FromSeconds(5)));
        var notifications = observed.ToArray();
        var actual = Assert.Single(notifications, identity => identity.ProcessId == first.ProcessId);
        Assert.Equal(job.ActiveProcessIds().Order(), notifications.Select(identity => identity.ProcessId).Order());
        Assert.Equal(first.ProcessId, actual.ProcessId);
        Assert.Equal(first.CreatedAtUtc, actual.CreatedAtUtc);
        Assert.Equal(first.ImageSha256, actual.ImageSha256);
        using var retained = Process.GetProcessById(first.ProcessId);
        using var caller = Process.GetCurrentProcess();
        Assert.True(job.ContainsProcessHandle(retained.SafeHandle));
        Assert.False(job.ContainsProcessHandle(caller.SafeHandle));
        observer.Dispose();
        var second = job.Start(fixture, [secondReady, "detached"], root,
            Guid.NewGuid(), Guid.NewGuid(), _ => { });
        await AwaitFileAsync(secondReady);
        var active = job.ActiveProcessIds();
        Assert.Contains(first.ProcessId, active);
        Assert.Contains(second.ProcessId, active);
        Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(notifications, observed.ToArray());
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
        var ready = Path.Combine(root, Guid.NewGuid() + ".json");
        var started = job.Start(fixture, [ready, "failed-observer"], root,
            Guid.NewGuid(), Guid.NewGuid(), _ => { });
        await called.Task.WaitAsync(TimeSpan.FromSeconds(10));
        observer.Dispose();
        Assert.Contains(observer.Failures, failure => failure.EndsWith(":IOException", StringComparison.Ordinal));
        await AwaitFileAsync(ready);
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
