using Xunit;
using NexusPipeline.Modules.Execution;
namespace NexusPipeline.Tests.Execution;


public sealed class PendingSystemActionTests
{

    [Fact]
    public void PendingSystemAction_CancelFailureKeepsPendingAndBlocksAdmission()
    {
        var store = new ExecutionStateStore();
        var execution = new RunningExecution
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = "queue",
            TargetId = "pending-queue",
            TargetName = "队列",
        };
        Assert.True(store.TryRegister(execution, new ExecutionAdmissionProfile(
            "queue",
            ExecutionConcurrencyClass.Standard,
            ExecutionResourceSet.Empty,
            "none"), out ExecutionAdmissionFailure? admissionFailure));
        Assert.Null(admissionFailure);
        PendingSystemAction? pending = store.Release(
            execution,
            new CompletionIntent(execution.Id, "队列", "shutdown"));
        Assert.NotNull(pending);
        Assert.True(store.TryBeginCancelPending(out PendingSystemAction? cancelling));
        Assert.Same(pending, cancelling);
        Assert.Equal(ExecutionGroupState.Cancelling, store.GroupState);

        Assert.False(store.CompleteCancelPending(pending, osCancelSucceeded: false));
        Assert.NotNull(store.CurrentSystemAction);
        Assert.Equal(ExecutionGroupState.Cancelling, store.GroupState);

        var blockedExecution = new RunningExecution
        {
            Kind = "queue",
            TargetId = "new-queue",
            TargetName = "新队列",
        };
        Assert.False(store.TryRegister(blockedExecution, new ExecutionAdmissionProfile(
            "queue",
            ExecutionConcurrencyClass.Standard,
            ExecutionResourceSet.Empty,
            "none"), out ExecutionAdmissionFailure? failure));
        Assert.Equal(ExecutionAdmissionFailureCode.PendingSystemAction, failure!.Code);

        Assert.True(store.CompleteCancelPending(pending, osCancelSucceeded: true));
        Assert.Null(store.CurrentSystemAction);
        Assert.Equal(ExecutionGroupState.Open, store.GroupState);
    }
}
