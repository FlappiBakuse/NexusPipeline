namespace NexusPipeline.Core.Execution;

public enum ExecutionStopReason { Completed, Cancelled, Interrupted, TotalBudgetExhausted, TargetUnavailable }

public interface IExecutionAttemptSession
{
    IReadOnlyList<string> StepIds { get; }
    bool HasEnteredEffect { get; }
    void PrepareConfigurationGroups();
    ExecutionStopReason? ReadStopRequest();
    ValueTask<bool> TrySkipAsync(string stepId) => ValueTask.FromResult(false);
    ValueTask<bool> TryRetryAsync(string stepId) => ValueTask.FromResult(false);
    void PrepareNextStep(string stepId);
    void EnterEffect(string stepId);
    Task<ExecutionStopReason?> ExecuteStepAsync(string stepId, CancellationToken hostStopping);
    Task CollectStepAsync(string stepId);
    Task RestoreStepAsync(string stepId);
    void FreezeDispatch(ExecutionStopReason reason);
    Task CollectRemainingAsync();
    void CloseUndispatched();
    void MarkApplied();
    Task RestoreRemainingAsync(ExecutionStopReason reason);
    AttemptReceipt Commit(ExecutionStopReason reason);
    Task QuarantineFailureAsync();
}
