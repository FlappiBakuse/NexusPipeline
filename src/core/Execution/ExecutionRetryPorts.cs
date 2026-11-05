namespace NexusPipeline.Core.Execution;

public sealed record PendingExecutionRetry(Guid RunId, Guid PreviousAttemptId, Guid NextAttemptId, Guid NextJournalId,
    Guid NextOperationId, int NextOrdinal, string IdempotencyKey, DateTimeOffset NotBefore, DateTimeOffset DeadlineUtc,
    long ConsumedMilliseconds, string FrozenPlanDigest, string PreviousReceiptDigest);

public interface IPreparedExecutionRetry : IDisposable
{
    PendingExecutionRetry Frozen { get; }
}

public interface IExecutionRetryStore
{
    // Implementations derive authority from the original frozen contract, stopped writers, recovery and actual receipt.
    IPreparedExecutionRetry? Prepare(Guid runId, Guid previousAttemptId, DateTimeOffset now);
    IPreparedExecutionRetry? ReadWaiting(Guid runId);
}

public interface IExecutionRetryAdmission
{
    IExecutionAdmissionReceipt Start(IPreparedExecutionRetry retry, DateTimeOffset now);
}
