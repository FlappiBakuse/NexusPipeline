namespace NexusPipeline.Core.Operations;

public sealed record OperationSnapshot(Guid OperationId, string Kind, string Status, Guid? InitialJournalId);

public interface IOperationQueries
{
    OperationSnapshot? Read(Guid operationId, string subject);
}
