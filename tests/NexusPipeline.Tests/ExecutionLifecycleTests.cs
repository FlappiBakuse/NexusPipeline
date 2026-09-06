using NexusPipeline;
using NexusPipeline.App.Commands;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using Xunit;

namespace NexusPipeline.Tests;

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
    public void ConfigRunSession_ProvidesExplicitLifecycleBoundary()
    {
        var session = new ConfigRunSession("script", userKey: null, configPath: "", hasJudgeScript: false);

        Assert.False(session.IsPrepared);
        Assert.True(session.Prepare(out string? error));
        Assert.Null(error);
        Assert.False(session.IsPrepared);
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
    public void ConfigRunSession_FinalizationOrder_IsSingleAndStable()
    {
        IReadOnlyList<ConfigRunSession.FinalizationStep> order = ConfigRunSession.BuildFinalizationOrder(
            canSync: true,
            hasJudgeScript: true,
            prepared: true);

        Assert.Equal(
            new[]
            {
                ConfigRunSession.FinalizationStep.Sync,
                ConfigRunSession.FinalizationStep.RestoreReplacements,
                ConfigRunSession.FinalizationStep.CleanupScriptArea,
                ConfigRunSession.FinalizationStep.RestoreConfig,
            },
            order);
    }

    [Fact]
    public void ConfigRunSession_FinalizationOrder_WithoutPreparedConfigSkipsSyncAndRestore()
    {
        IReadOnlyList<ConfigRunSession.FinalizationStep> order = ConfigRunSession.BuildFinalizationOrder(
            canSync: false,
            hasJudgeScript: true,
            prepared: false);

        Assert.Equal(
            new[]
            {
                ConfigRunSession.FinalizationStep.RestoreReplacements,
                ConfigRunSession.FinalizationStep.CleanupScriptArea,
            },
            order);
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
