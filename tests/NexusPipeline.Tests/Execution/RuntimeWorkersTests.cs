using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Modules.Scripts;
using Xunit;

namespace NexusPipeline.Tests.Execution;

public sealed class RuntimeWorkersTests
{
    [Fact]
    public async Task ObserverFailureCompletesFinalCallWithoutReplacingAcceptedMarker()
    {
        var script = Script();
        var judge = new SessionJudge(script);
        judge.ApplyJudgeResult("success", "accepted", "original", [], _ => { });
        await using var workers = Workers(script, judge, default,
            (_, _) => Task.FromException<JudgeScriptResult>(new InvalidOperationException("owned observer failure")));
        workers.RequestFinalJudge();
        await Consume(workers);
        Assert.True(workers.FinalJudgeCompleted);
        Assert.True(judge.IsMarker);
        Assert.False(judge.IsFailure);
        Assert.Equal("original", judge.NotifyText);
        workers.RequestFinalJudge();
        Assert.False(workers.TryQueuePendingFinalJudge());
    }

    [Fact]
    public async Task BusyWorkerCapturesOnceAndQueuesExactlyOneFinalCall()
    {
        var script = Script();
        var judge = new SessionJudge(script);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int captured = 0, calls = 0, finalCalls = 0;
        await using var workers = Workers(script, judge, default, async (final, token) =>
        {
            Interlocked.Increment(ref calls);
            if (final) Interlocked.Increment(ref finalCalls);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new JudgeScriptResult { Status = "partial", Reason = "owned", NotifyText = "once" };
        }, () => captured++);
        Assert.True(workers.QueueJudge(final: false));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(workers.QueueJudge(final: false));
        workers.RequestFinalJudge();
        workers.RequestFinalJudge();
        Assert.Equal(1, captured);
        release.SetResult();
        await Consume(workers);
        Assert.True(workers.TryQueuePendingFinalJudge());
        await Consume(workers);
        Assert.Equal(2, captured);
        Assert.Equal(2, calls);
        Assert.Equal(1, finalCalls);
        Assert.True(workers.FinalJudgeCompleted);
        Assert.False(workers.TryQueuePendingFinalJudge());
        Assert.Equal(SessionJudge.JudgeOutcome.Partial, judge.MarkerOutcome);
    }

    [Fact]
    public async Task CancelledAttemptDiscardsLateSuccessWithoutNotificationOrReplacement()
    {
        var script = Script();
        var judge = new SessionJudge(script);
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int replace = 0;
        await using var workers = Workers(script, judge, stop.Token, async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return new JudgeScriptResult { Status = "success", Reason = "late", NotifyText = "must not escape", ReplaceConfigs = ["configuration.json"] };
        }, replace: _ => replace++);
        Assert.True(workers.QueueJudge(final: false));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        release.SetResult();
        await Consume(workers);
        Assert.False(judge.IsMarker);
        Assert.False(judge.IsFailure);
        Assert.Equal("", judge.NotifyText);
        Assert.Equal(0, replace);
        Assert.False(workers.QueueJudge(final: true));
    }

    private static ScriptInstance Script() => new() { Id = "owned", Name = "owned", RootPath = AppContext.BaseDirectory,
        JudgeScriptEnabled = true, JudgeScript = "owned" };
    private static RuntimeWorkers Workers(ScriptInstance script, SessionJudge judge, CancellationToken token,
        Func<bool, CancellationToken, Task<JudgeScriptResult>> observer, Action? capture = null, Action<List<string>>? replace = null) =>
        new("owned-attempt", 1, token, "test", "owned", judge, _ => { }, generation =>
        {
            capture?.Invoke();
            return new("owned-attempt", 1, generation, "owned log", DateTime.Now, script, null, AppContext.BaseDirectory, "{}", []);
        }, replace ?? (_ => { }), _ => { }, taskObserver: observer);
    private static async Task Consume(RuntimeWorkers workers)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await workers.ConsumeJudgeResultAsync()) await Task.Delay(10, timeout.Token);
    }
}
