using Xunit;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Modules.Execution;
namespace NexusPipeline.Tests.Execution;


public sealed class RuntimeWorkerTests
{

    [Fact]
    public async Task SingleFlightWorker_RejectsOverlapAndReturnsCompletion()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = new SingleFlightWorker<int, int>(async (value, token) =>
        {
            started.TrySetResult(true);
            await release.Task.WaitAsync(token);
            return value * 2;
        });

        Assert.True(worker.TryStart(21));
        await started.Task;
        Assert.False(worker.TryStart(22));

        release.SetResult(true);
        await EventuallyAsync(() => worker.TryTakeCompleted(out int value, out Exception? error)
            && error is null
            && value == 42);
    }

    [Fact]
    public async Task RunBudgetWatchdog_ExpiresIndependentlyOfMonitorTicks()
    {
        var budget = new RunBudget(1, DateTime.Now.AddMinutes(-2));
        var expired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var watchdog = new RunBudgetWatchdog(budget, CancellationToken.None, () => expired.TrySetResult(true));

        watchdog.Start();
        Assert.True(await Task.WhenAny(expired.Task, Task.Delay(TimeSpan.FromSeconds(2))) == expired.Task);
        Assert.True(budget.IsExpired);
    }

    [Fact]
    public async Task RunBudgetWatchdog_DisabledBudgetCanBeDisposed()
    {
        var budget = new RunBudget(-1, DateTime.Now);
        var expired = false;
        var watchdog = new RunBudgetWatchdog(budget, CancellationToken.None, () => expired = true);

        watchdog.Start();
        await watchdog.DisposeAsync();

        Assert.False(expired);
        Assert.False(budget.IsExpired);
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.True(condition(), "条件在超时时间内未满足");
    }
}
