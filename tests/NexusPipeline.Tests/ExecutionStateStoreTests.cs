using NexusPipeline.App.Commands;
using NexusPipeline.Models;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class ExecutionStateStoreTests
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
        Assert.NotNull(secondFailure);
        Assert.Equal(ExecutionAdmissionFailureCode.StandardQueueAlreadyRunning, secondFailure!.Code);
        Assert.Equal("已有其他调度队列正在运行，当前队列「备用队列」暂不能并行执行", secondFailure.Message);
        Assert.Same(first, store.Find(first.Id));
        Assert.Null(store.Find(second.Id));

        store.Unregister(first);
        Assert.Empty(store.Active);
        Assert.Same(first, store.FindAny(first.Id));

        PendingSystemAction pending = CreatePending(store, "sleep", "每日队列");
        Assert.NotNull(store.CurrentSystemAction);
        Assert.True(store.TryBeginCancelPending(out PendingSystemAction? taken));
        Assert.Same(pending, taken);
        Assert.True(store.CompleteCancelPending(taken!, osCancelSucceeded: true));
        Assert.Null(store.CurrentSystemAction);

        var armStore = new ExecutionStateStore();
        PendingSystemAction armPending = CreatePending(armStore, "shutdown", "arm-queue");
        Assert.True(armStore.TryArm(armPending));
        Assert.True(armStore.TryBeginCancelPending(out PendingSystemAction? canceled));
        Assert.True(armStore.CompleteCancelPending(canceled!, osCancelSucceeded: true));
        Assert.False(armStore.TryArm(armPending));
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
        var first = new RunningExecution
        {
            Kind = "queue",
            TargetId = "queue-1",
            TargetName = "模拟器队列A",
        };
        var second = new RunningExecution
        {
            Kind = "queue",
            TargetId = "queue-2",
            TargetName = "模拟器队列B",
        };

        Assert.True(store.TryRegister(first, emulator, out _));
        Assert.True(store.TryRegister(second, emulator, out _));

        Assert.Null(store.Release(first, new CompletionIntent(first.Id, first.TargetName, "shutdown")));
        Assert.Null(store.CurrentSystemAction);

        PendingSystemAction? pending = store.Release(second, new CompletionIntent(second.Id, second.TargetName, "shutdown"));
        Assert.NotNull(pending);
        Assert.Equal("shutdown", pending!.Action);
        Assert.Equal("模拟器队列A、模拟器队列B", pending.QueueName);
        Assert.Equal(pending.Action, store.CurrentSystemAction!.Action);
        Assert.Equal(pending.QueueName, store.CurrentSystemAction.QueueName);

        var blocked = new RunningExecution
        {
            Kind = "queue",
            TargetId = "queue-3",
            TargetName = "新队列",
        };
        Assert.False(store.TryRegister(blocked, emulator, out ExecutionAdmissionFailure? blockedFailure));
        Assert.Equal(ExecutionAdmissionFailureCode.PendingSystemAction, blockedFailure!.Code);

        Assert.True(store.TryBeginCancelPending(out PendingSystemAction? canceled));
        Assert.Same(pending, canceled);
        Assert.True(store.CompleteCancelPending(canceled!, osCancelSucceeded: true));
        Assert.True(store.TryRegister(blocked, emulator, out _));
    }

    [Fact]
    public void SameCompletionIntentMergesAndDifferentActionIsRejected()
    {
        var store = new ExecutionStateStore();
        ExecutionAdmissionProfile shutdown = new(
            "queue",
            ExecutionConcurrencyClass.EmulatorOnly,
            ExecutionResourceSet.Empty,
            "shutdown");
        ExecutionAdmissionProfile reboot = shutdown with { CompletionAction = "reboot" };
        var first = new RunningExecution
        {
            Kind = "queue",
            TargetId = "queue-1",
            TargetName = "队列A",
        };
        var second = new RunningExecution
        {
            Kind = "queue",
            TargetId = "queue-2",
            TargetName = "队列B",
        };

        Assert.True(store.TryRegister(first, shutdown, out _));
        Assert.True(store.TryRegister(second, shutdown, out _));
        Assert.Null(store.Release(first, new CompletionIntent(first.Id, first.TargetName, "shutdown")));

        var candidate = new RunningExecution
        {
            Kind = "queue",
            TargetId = "queue-3",
            TargetName = "队列C",
        };
        Assert.False(store.TryRegister(candidate, reboot, out ExecutionAdmissionFailure? failure));
        Assert.Equal(ExecutionAdmissionFailureCode.ExecutionGroupClosing, failure!.Code);
        Assert.Equal(ExecutionGroupState.Closing, store.GroupState);

        PendingSystemAction? pending = store.Release(second, new CompletionIntent(second.Id, second.TargetName, "shutdown"));
        Assert.NotNull(pending);
        Assert.True(store.TryBeginCancelPending(out PendingSystemAction? canceled));
        Assert.True(store.CompleteCancelPending(canceled!, osCancelSucceeded: true));
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
