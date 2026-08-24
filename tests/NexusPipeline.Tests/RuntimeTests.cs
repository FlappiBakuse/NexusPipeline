using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using NexusPipeline.Utilities;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class RuntimeTests
{
    [Fact]
    public void StableExitWindow_RequiresContinuousEmptyWindow()
    {
        var window = new StableExitWindow(TimeSpan.FromSeconds(3));
        DateTime start = new(2026, 8, 24, 9, 0, 0, DateTimeKind.Local);

        Assert.False(window.Observe(hasOwnedProcess: false, start));
        Assert.False(window.Observe(hasOwnedProcess: true, start.AddSeconds(2)));
        Assert.False(window.Observe(hasOwnedProcess: false, start.AddSeconds(3)));
        Assert.True(window.Observe(hasOwnedProcess: false, start.AddSeconds(6)));
        Assert.True(window.IsStable);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(1000)]
    [InlineData(3000)]
    [InlineData(5000)]
    public void StableExitWindow_RejectsRestartTimingsCoveredByHarness(int restartDelayMs)
    {
        var window = new StableExitWindow(TimeSpan.FromSeconds(SystemActions.StableExitSeconds));
        DateTime start = new(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc);

        Assert.False(window.Observe(hasOwnedProcess: false, start));
        Assert.False(window.Observe(hasOwnedProcess: true, start.AddMilliseconds(restartDelayMs)));
        Assert.False(window.Observe(
            hasOwnedProcess: false,
            start.AddMilliseconds(restartDelayMs)));
        Assert.True(window.Observe(
            hasOwnedProcess: false,
            start.AddMilliseconds(restartDelayMs + SystemActions.StableExitSeconds * 1000)));
    }

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

    [Fact]
    public void ProcessIdentity_DoesNotMatchReusedPid()
    {
        DateTime start = new(2026, 8, 24, 9, 0, 0, DateTimeKind.Local);
        var identity = new ProcessIdentity(100, start, "script.exe");

        Assert.True(identity.Matches(new ProcessIdentity(100, start, "script.exe")));
        Assert.False(identity.Matches(new ProcessIdentity(100, start.AddTicks(1), "script.exe")));
        Assert.False(identity.Matches(new ProcessIdentity(101, start, "script.exe")));
        Assert.False(identity.Matches(new ProcessIdentity(100, start, "other.exe")));
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
