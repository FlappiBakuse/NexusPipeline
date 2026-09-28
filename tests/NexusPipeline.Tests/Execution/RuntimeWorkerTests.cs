using Xunit;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Modules.Scripts;
namespace NexusPipeline.Tests.Execution;


public sealed class RuntimeWorkerTests
{

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StallConsumesFinalDecisionEvenWhenPeriodicCallStartedThisTick(bool periodicSameTick)
    {
        var script = new ScriptInstance { JudgeScriptEnabled = true, JudgeScript = "pending" };
        var judge = new SessionJudge(script);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var periodicStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var workers = new RuntimeWorkers("attempt", 1, CancellationToken.None, "test", "owned",
            judge, _ => { }, generation => new JudgeSnapshot("attempt", 1, generation, "", DateTime.Now,
                script, null, "", "{}", []), _ => { }, _ => { }, taskObserver: async (final, token) =>
            {
                if (!final) { periodicStarted.SetResult(true); await release.Task.WaitAsync(token); }
                return new JudgeScriptResult { Status = final ? "success" : "pending", Reason = "final-observed" };
            });
        if (periodicSameTick) { Assert.True(workers.QueueJudge(false)); await periodicStarted.Task; }
        var terminator = new AttemptTerminator(workers, judge, _ => { });
        Assert.Null(terminator.OnStall(new(true, "stall"), periodicSameTick));
        release.SetResult(true);
        RunAttemptResult? result = null;
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (result is null && DateTime.UtcNow < deadline)
        {
            await workers.ConsumeJudgeResultAsync();
            workers.TryQueuePendingFinalJudge();
            result = terminator.TryApplyFinalDecision();
            await Task.Delay(10);
        }
        Assert.NotNull(result);
        Assert.Equal("success", result!.Status);
        Assert.Contains("final-observed", result.Reason);
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
        int discardedCaptures = 0;
        Assert.False(worker.TryStart(() => { discardedCaptures++; return 22; }));
        Assert.Equal(0, discardedCaptures);

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
