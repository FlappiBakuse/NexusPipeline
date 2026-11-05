namespace NexusPipeline.Core.Execution;

public sealed record AttemptReceipt(Guid JournalId, Guid OperationId, string PlanDigest, string SourceReceiptDigest, AttemptFacts Facts);

public interface IAttemptReceiptReader
{
    AttemptReceipt? Read(Guid attemptId);
}
