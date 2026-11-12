using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal sealed class NotificationAttemptReads(
    RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease) : IDisposable
{
    private readonly HashSet<Guid> _seen = [];
    private readonly List<IDisposable> _held = [];
    private bool _disposed;

    internal void HoldJournal(Guid journal)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lease.VerifyHeld(instance);
        if (!_seen.Add(journal)) return;
        var held = lease.BindAttemptReads(database).HoldTerminal(instance, database, contracts, journal);
        if (held is not null) _held.Add(held);
    }

    internal void HoldReceipt(BlobReference reference)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var receipt = new NotificationReceiptCodec(instance, database, contracts).ReadMetadata(reference, connection, transaction);
        HoldJournal(receipt.Document.Receipt.JournalId);
        transaction.Commit();
    }

    internal void HoldDelivery(Guid operation)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT a.journal_id FROM notification_deliveries d " +
            "JOIN attempt_receipts a ON a.attempt_id=d.source_attempt_id WHERE d.operation_id=$operation;";
        command.Parameters.AddWithValue("$operation", operation.ToString("D"));
        if (command.ExecuteScalar() is string journal) HoldJournal(Guid.Parse(journal));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
        _held.Clear(); _seen.Clear();
    }
}
