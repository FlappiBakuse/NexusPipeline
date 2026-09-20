using Xunit;
using NexusPipeline.Modules.Execution;
namespace NexusPipeline.Tests.Execution;


public sealed class AttemptHookPolicyTests
{
    [Fact]
    public void PostRunFailure_PreservesMainFatalStateAndCombinesBothReasons()
    {
        RunAttemptResult main = RunAttemptResult.Fatal("主脚本致命失败");
        main.NotifyText = "主脚本通知";
        RunAttemptResult post = RunAttemptResult.Failed("后置脚本失败");

        RunAttemptResult merged = RunAttemptResult.MergePostRun(main, post);

        Assert.Equal("failed", merged.Status);
        Assert.True(merged.IsFatal);
        Assert.Contains("主脚本致命失败", merged.Reason);
        Assert.Contains("后置脚本失败", merged.Reason);
        Assert.Equal("主脚本通知", merged.NotifyText);
    }

    [Fact]
    public void PostRunFinalOnly_UsesActualMainOutcomeInsteadOfAttemptNumber()
    {
        RetryPolicy policy = new(3);

        Assert.False(AttemptLifecycle.ShouldRunPostRun(finalOnly: true, attemptNumber: 1, policy, RunAttemptResult.Failed("可重试")));
        Assert.True(AttemptLifecycle.ShouldRunPostRun(finalOnly: true, attemptNumber: 1, policy, RunAttemptResult.Success("提前成功")));
        Assert.True(AttemptLifecycle.ShouldRunPostRun(finalOnly: true, attemptNumber: 1, policy, RunAttemptResult.Fatal("提前致命失败")));
        Assert.True(AttemptLifecycle.ShouldRunPostRun(finalOnly: true, attemptNumber: 3, policy, RunAttemptResult.Failed("达到上限")));
        Assert.True(AttemptLifecycle.ShouldRunPostRun(finalOnly: true, attemptNumber: 1, policy, RunAttemptResult.Partial("局部完成")));
    }

    [Fact]
    public void Partial_IsTerminalAndPostRunKeepsOrReplacesItsStatus()
    {
        RetryPolicy policy = new(3);
        RunAttemptResult main = RunAttemptResult.Partial("主流程部分完成");

        Assert.False(policy.ShouldRetry(1, main));
        RunAttemptResult postSuccess = RunAttemptResult.MergePostRun(main, RunAttemptResult.Success("后置完成"));
        Assert.Equal("partial", postSuccess.Status);
        Assert.False(postSuccess.IsFatal);

        RunAttemptResult postFailure = RunAttemptResult.MergePostRun(main, RunAttemptResult.Failed("后置失败"));
        Assert.Equal("failed", postFailure.Status);
        Assert.Contains("后置失败", postFailure.Reason);
    }

    [Fact]
    public void PreRunOnceOnly_SkipsOnlyAfterSuccessfulPreRun()
    {
        Assert.True(AttemptLifecycle.ShouldRunPreRun(hasScript: true, onceOnly: true, completedSuccessfully: false));
        Assert.True(AttemptLifecycle.ShouldRunPreRun(hasScript: true, onceOnly: false, completedSuccessfully: true));
        Assert.False(AttemptLifecycle.ShouldRunPreRun(hasScript: true, onceOnly: true, completedSuccessfully: true));
        Assert.False(AttemptLifecycle.ShouldRunPreRun(hasScript: false, onceOnly: false, completedSuccessfully: false));
    }
}
