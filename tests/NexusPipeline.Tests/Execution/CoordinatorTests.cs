using Xunit;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.History;
using NexusPipeline.Modules.Scripts;

namespace NexusPipeline.Tests.Execution;

/// <summary>执行协调核心链路：准入登记、重试预算、宿主错误记录。</summary>
public sealed class CoordinatorTests
{
    [Fact]
    public void RegistrationGuardsAndLifecycleHistoryArePreserved()
    {
        var store = new ExecutionStateStore();
        var first = new RunningExecution
        {
            Kind = "queue",
            TargetId = "queue-1",
            TargetName = "每日队列",
        };
        var second = new RunningExecution
        {
            Kind = "queue",
            TargetId = "queue-2",
            TargetName = "备用队列",
        };
        ExecutionAdmissionProfile standard = new(
            "queue",
            ExecutionConcurrencyClass.Standard,
            ExecutionResourceSet.Empty,
            "none");

        Assert.True(store.TryRegister(first, standard, out ExecutionAdmissionFailure? firstFailure));
        Assert.Null(firstFailure);
        Assert.False(store.TryRegister(second, standard, out ExecutionAdmissionFailure? secondFailure));
        Assert.Equal(ExecutionAdmissionFailureCode.StandardQueueAlreadyRunning, secondFailure!.Code);
        Assert.Same(first, store.Find(first.Id));
        Assert.Null(store.Find(second.Id));

        store.Unregister(first);
        Assert.Empty(store.Active);
        Assert.Same(first, store.FindAny(first.Id));

        PendingSystemAction pending = CreatePending(store, "sleep", "每日队列");
        Assert.True(store.TryBeginCancelPending(out PendingSystemAction? taken));
        Assert.Same(pending, taken);
        Assert.True(store.CompleteCancelPending(taken!, osCancelSucceeded: true));
        Assert.Null(store.CurrentSystemAction);
    }

    [Fact]
    public void CompletionIsArmedOnlyWhenLastExecutionReleases()
    {
        var store = new ExecutionStateStore();
        ExecutionAdmissionProfile emulator = new(
            "queue",
            ExecutionConcurrencyClass.EmulatorOnly,
            ExecutionResourceSet.Empty,
            "shutdown");
        var first = new RunningExecution { Kind = "queue", TargetId = "queue-1", TargetName = "模拟器队列A" };
        var second = new RunningExecution { Kind = "queue", TargetId = "queue-2", TargetName = "模拟器队列B" };

        Assert.True(store.TryRegister(first, emulator, out _));
        Assert.True(store.TryRegister(second, emulator, out _));
        Assert.Null(store.Release(first, new CompletionIntent(first.Id, first.TargetName, "shutdown")));
        Assert.Null(store.CurrentSystemAction);

        PendingSystemAction? pending = store.Release(second, new CompletionIntent(second.Id, second.TargetName, "shutdown"));
        Assert.NotNull(pending);
        Assert.Equal("shutdown", pending!.Action);
        Assert.Equal("模拟器队列A、模拟器队列B", pending.QueueName);

        var blocked = new RunningExecution { Kind = "queue", TargetId = "queue-3", TargetName = "新队列" };
        Assert.False(store.TryRegister(blocked, emulator, out ExecutionAdmissionFailure? blockedFailure));
        Assert.Equal(ExecutionAdmissionFailureCode.PendingSystemAction, blockedFailure!.Code);

        Assert.True(store.TryBeginCancelPending(out PendingSystemAction? canceled));
        Assert.True(store.CompleteCancelPending(canceled!, osCancelSucceeded: true));
        Assert.True(store.TryRegister(blocked, emulator, out _));
    }

    [Fact]
    public void EvaluateCandidateUsesRegistrationPolicyWithoutMutatingState()
    {
        var store = new ExecutionStateStore();
        var active = new RunningExecution { Kind = "queue", TargetId = "queue-1", TargetName = "当前队列" };
        var candidate = new RunningExecution { Kind = "queue", TargetId = "queue-2", TargetName = "待检查队列" };
        ExecutionAdmissionProfile profile = new(
            "queue",
            ExecutionConcurrencyClass.Standard,
            ExecutionResourceSet.Empty,
            "none");

        Assert.True(store.TryRegister(active, profile, out _));
        ExecutionAdmissionFailure? explained = store.EvaluateCandidate(
            candidate.Kind,
            candidate.TargetId,
            candidate.TargetName,
            profile);

        Assert.Equal(ExecutionAdmissionFailureCode.StandardQueueAlreadyRunning, explained!.Code);
        Assert.Single(store.Active);
        Assert.Null(store.Find(candidate.Id));
    }

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
        now = now.AddSeconds(30);
        Assert.Equal(30, budget.RemainingCommandSeconds(30));
        now = now.AddSeconds(30);
        Assert.True(budget.IsExpired);
    }

    [Fact]
    public void CoordinatorException_ProducesSyntheticFailedHistoryRecord()
    {
        var script = new ScriptInstance { Id = "script", Name = "脚本" };

        RunRecord record = ExecutionRunner.CreateHostErrorRecord(
            script,
            "manual",
            "queue",
            "队列",
            "user",
            new InvalidOperationException("协调器异常"));

        Assert.Equal("failed", record.Status);
        Assert.Equal("script", record.ScriptInstanceId);
        Assert.Equal("user", record.UserName);
        Assert.Contains("协调器异常", record.ResultDetail);
    }

    private static PendingSystemAction CreatePending(ExecutionStateStore store, string action, string queueName)
    {
        var execution = new RunningExecution
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = "queue",
            TargetId = Guid.NewGuid().ToString("N"),
            TargetName = queueName,
        };
        ExecutionAdmissionProfile profile = new(
            "queue",
            ExecutionConcurrencyClass.Standard,
            ExecutionResourceSet.Empty,
            "none");
        Assert.True(store.TryRegister(execution, profile, out ExecutionAdmissionFailure? failure));
        Assert.Null(failure);
        PendingSystemAction? pending = store.Release(
            execution,
            new CompletionIntent(execution.Id, queueName, action));
        Assert.NotNull(pending);
        return pending!;
    }
}
