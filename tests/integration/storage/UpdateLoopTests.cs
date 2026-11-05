using NexusPipeline.Core.Maintenance;
using NexusPipeline.Host.Lifecycle;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class UpdateLoopTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandlesNetworkCancellation(bool stopHost)
    {
        var clock = new CompletionControlledClock();
        using var stopping = new CancellationTokenSource();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var loop = HostAutomaticUpdates.RunLoopAsync(() =>
        {
            calls++;
            if (calls == 1)
            {
                if (stopHost) stopping.Cancel();
                first.TrySetResult();
                throw new TaskCanceledException("fixture_network_timeout");
            }
            return Task.FromResult(true);
        }, code => failure.TrySetResult(code), clock, stopping.Token);
        clock.Advance(AutomaticUpdatePolicy.FirstCheckDelay);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (stopHost)
        {
            await loop.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(failure.Task.IsCompleted);
            Assert.Equal(1, calls);
            return;
        }
        Assert.Equal("automatic_update_deadline", await failure.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        await Task.Delay(10);
        Assert.Equal(1, calls);
        Assert.False(loop.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(30));
        await loop.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, calls);
    }
}
