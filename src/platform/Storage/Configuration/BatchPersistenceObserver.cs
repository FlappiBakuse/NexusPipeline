namespace NexusPipeline.Platform.Storage.Configuration;

public enum BatchPersistencePoint { Prepared, OperationAccepted, ItemPublished, TransactionCommitted }

public interface IBatchPersistenceObserver
{
    void Reached(BatchPersistencePoint point, Guid operationId, int resourceOrdinal);
}
