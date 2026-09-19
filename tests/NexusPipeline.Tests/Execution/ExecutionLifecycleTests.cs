using Xunit;
using NexusPipeline.Modules.Execution;
namespace NexusPipeline.Tests.Execution;


public sealed class ExecutionLifecycleTests
{
    [Fact]
    public void RetryPolicy_OnlyRetriesRecoverableFailures()
    {
        var policy = new RetryPolicy(2);

        Assert.True(policy.ShouldRetry(1, RunAttemptResult.Failed("retry")));
        Assert.False(policy.ShouldRetry(1, RunAttemptResult.Fatal("fatal")));
        Assert.False(policy.ShouldRetry(2, RunAttemptResult.Failed("last")));
    }

    [Fact]
    public void RunBudget_CentralizesElapsedRemainingAndCommandCap()
    {
        DateTime now = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Local);
        var budget = new RunBudget(1, now, () => now);

        Assert.Equal(60, budget.RemainingSeconds);
        Assert.False(budget.IsExpired);
        now = now.AddSeconds(12.25);
        Assert.Equal(12.25, budget.ElapsedSeconds, precision: 2);
        Assert.Equal(47.75, budget.RemainingSeconds, precision: 2);
        Assert.Equal(30, budget.RemainingCommandSeconds(30));
        Assert.Equal(48, budget.RemainingCommandSeconds(60));
        now = now.AddSeconds(48);
        Assert.True(budget.IsExpired);
        Assert.Equal(1, budget.RemainingCommandSeconds(30));
    }

    [Fact]
    public void RunAttemptFinalizer_GameCleanupPolicy_PreservesFailureAndForceCloseSemantics()
    {
        Assert.True(RunAttemptFinalizer.ShouldCloseGame(RunAttemptResult.Failed("failed"), forceCloseGame: false));
        Assert.True(RunAttemptFinalizer.ShouldCloseGame(RunAttemptResult.Cancelled("cancelled"), forceCloseGame: true));
        Assert.False(RunAttemptFinalizer.ShouldCloseGame(RunAttemptResult.Cancelled("cancelled"), forceCloseGame: false));
        Assert.False(RunAttemptFinalizer.ShouldCloseGame(RunAttemptResult.Success("success"), forceCloseGame: true));
    }
}
