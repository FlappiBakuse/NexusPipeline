using NexusPipeline.ControlPlane.Http;
using Xunit;

namespace NexusPipeline.Tests.ControlPlane;

public sealed class RemoteAuthenticationThrottleTests
{
    [Fact]
    public async Task SuccessfulRequestCannotEraseConcurrentCompletedFailureWindow()
    {
        var throttle = new RemoteAuthenticationThrottle();
        long now = DateTime.UtcNow.Ticks;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var success = Task.Factory.StartNew(() => throttle.Authorize("same-address", () =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return true;
        }, now, out _), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var failures = Task.Factory.StartNew(() =>
        {
            for (int count = 0; count < 5; count++) Assert.False(throttle.Authorize("same-address", () => false, now, out _));
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try { await Task.WhenAny(failures, Task.Delay(150)); }
        finally { release.Set(); }
        Assert.True(await success);
        await failures;
        Assert.False(throttle.Authorize("same-address", () => true, now, out _));
    }

    [Fact]
    public async Task ConcurrentFailuresLockEachAddressAndSuccessCannotBypassItsWindow()
    {
        var throttle = new RemoteAuthenticationThrottle();
        long now = DateTime.UtcNow.Ticks;
        for (int round = 0; round < 100; round++)
        {
            string ip = "fixture-" + round;
            using var ready = new CountdownEvent(5);
            using var release = new ManualResetEventSlim();
            var requests = Enumerable.Range(0, 5).Select(index => Task.Factory.StartNew(() =>
            {
                ready.Signal(); release.Wait();
                Assert.False(throttle.Authorize(ip, () => false, now, out _));
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            try { Assert.True(ready.Wait(TimeSpan.FromSeconds(5))); }
            finally { release.Set(); }
            await Task.WhenAll(requests);
            Assert.False(throttle.Authorize(ip, () => true, now, out _));
            Assert.True(throttle.Authorize(ip, () => true, now + TimeSpan.FromSeconds(60).Ticks, out _));
            for (int count = 0; count < 4; count++) Assert.False(throttle.Authorize(ip, () => false, now + TimeSpan.FromSeconds(61).Ticks, out _));
            Assert.True(throttle.Authorize(ip, () => true, now + TimeSpan.FromSeconds(61).Ticks, out _));
        }
        Assert.True(throttle.Authorize("independent", () => true, now, out _));
        Assert.True(throttle.Authorize("fixture-0", () => true, now + TimeSpan.FromMinutes(20).Ticks, out _));
    }
}
