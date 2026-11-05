using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Operations;

public sealed class OperationQueryService : IOperationQueries
{
    private readonly RuntimeInstance _instance;
    private readonly InstanceLease _lease;
    private readonly StateDatabase _database;

    public OperationQueryService(RuntimeInstance instance, InstanceLease lease, StateDatabase database)
    {
        lease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot)
            throw new IOException("operation_query_instance_unbound");
        _instance = instance; _lease = lease; _database = database;
    }

    public OperationSnapshot? Read(Guid operationId, string subject)
    {
        _lease.VerifyHeld(_instance);
        if (operationId == Guid.Empty || subject is not { Length: >= 1 and <= 256 } || subject.Any(char.IsControl))
            throw new ArgumentException("operation_query_identity_invalid");
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var result = OperationRows.ReadOwned(connection, transaction, operationId, subject);
        transaction.Commit(); return result;
    }
}
