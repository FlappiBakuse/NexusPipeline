using System.Diagnostics;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class PublicationContextTests
{
    [Theory(DisplayName = "Every Queued Publication Uses Its Own Caller Context And Suppressed Flow Stays Empty Even After Failure")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturesEachContext(bool firstFails)
    {
        var queue = new ExecutionPublicationQueue(); var marker = new AsyncLocal<string?>();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var first = Task.Run(() =>
        {
            marker.Value = "first";
            return queue.Run(() =>
            {
                Assert.Equal("first", marker.Value); marker.Value = "first-dirty"; entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                if (firstFails) throw new IOException("fixture_first_failed");
                return marker.Value;
            });
        });
        Task<string?>? second = null, suppressed = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            second = Task.Run<string?>(() =>
            {
                marker.Value = "second";
                var observed = queue.Run(() => { var value = marker.Value; marker.Value = "second-dirty"; return value; });
                Assert.Equal("second", marker.Value); return observed;
            });
            suppressed = Task.Run<string?>(() =>
            {
                marker.Value = "suppressed-source";
                using (ExecutionContext.SuppressFlow()) return queue.Run(() => marker.Value);
            });
            var clock = Stopwatch.StartNew();
            while (queue.PendingCount != 3 && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(1);
            Assert.Equal(3, queue.PendingCount);
        }
        finally { release.Set(); }
        if (firstFails) Assert.Equal("fixture_first_failed", (await Assert.ThrowsAsync<IOException>(async () => await first)).Message);
        else Assert.Equal("first-dirty", await first);
        Assert.Equal("second", await second!); Assert.Null(await suppressed!);
        queue.Close(() => { }); await queue.Released.WaitAsync(TimeSpan.FromSeconds(10));
    }
    [Fact(DisplayName = "Sixty Four Accepted Publications Drain Before Release And An Additional Publication Is Rejected")]
    public async Task BoundsPendingWork()
    {
        var queue = new ExecutionPublicationQueue();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var completed = 0;
        var callers = Enumerable.Range(0, 64).Select(index => new Thread(() =>
        {
            try
            {
                queue.Run(() =>
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                    Interlocked.Increment(ref completed); return index;
                });
            }
            catch (Exception error) { errors.Enqueue(error); }
        }) { IsBackground = true }).ToArray();
        foreach (var caller in callers) caller.Start();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            var clock = Stopwatch.StartNew();
            while (queue.PendingCount != 64 && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(1);
            Assert.Equal(64, queue.PendingCount);
            Assert.Equal("execution_publication_capacity_exceeded", Assert.Throws<IOException>(() => queue.Run(() => true)).Message);
            queue.Close(() => Assert.Equal(64, completed));
        }
        finally { release.Set(); }
        foreach (var caller in callers) Assert.True(caller.Join(TimeSpan.FromSeconds(10)));
        queue.Close(() => { }); await queue.Released.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(errors); Assert.Equal(64, completed);
    }

    [Fact(DisplayName = "Idle Publication Closes Once And Rejects Reentrant Or Subsequent Work")]
    public async Task ClosesIdleQueue()
    {
        var queue = new ExecutionPublicationQueue();
        Assert.Equal("execution_publication_reentrancy", queue.Run(() =>
            Assert.Throws<IOException>(() => queue.Run(() => true)).Message));
        Assert.True(SpinWait.SpinUntil(() => queue.PendingCount == 0, TimeSpan.FromSeconds(10)));
        var releases = 0;
        queue.Close(() => Interlocked.Increment(ref releases));
        Assert.Equal(1, releases);
        queue.Close(() => Interlocked.Increment(ref releases));
        await queue.Released.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, releases);
        Assert.Throws<ObjectDisposedException>(() => queue.Run(() => true));
    }
}
