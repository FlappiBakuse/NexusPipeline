using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal sealed class NotificationAttemptReads(
    RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease) : IDisposable
{
    private readonly HashSet<Guid> _seen = [];
    private readonly List<IDisposable> _held = [];
    private readonly Dictionary<string, (IDisposable Lease, LinkedListNode<string> Node)> _metadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _recent = new();
    private bool _disposed;

    internal BlobReadScope CaptureMetadata()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return BlobReadScope.Capture(instance, HoldMetadata);
    }

    private void HoldMetadata(string path)
    {
        if (_metadata.TryGetValue(path, out var current))
        {
            _recent.Remove(current.Node); _recent.AddLast(current.Node); return;
        }
        var held = BlobReadStream.Retain(path, out var reference);
        if (!reference.RelativePath.StartsWith("blobs/plans/", StringComparison.Ordinal) || reference.SizeBytes > 65536)
        { held.Dispose(); return; }
        try
        {
            if (_metadata.Count == BlobReadScope.MaximumReferences)
            {
                var oldest = _recent.First!; var removed = _metadata[oldest.Value];
                _metadata.Remove(oldest.Value); _recent.RemoveFirst(); removed.Lease.Dispose();
            }
            _metadata.Add(path, (held, _recent.AddLast(path)));
        }
        catch { held.Dispose(); throw; }
    }

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
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records | DatabaseDomain.Notifications);
        using var transaction = DatabaseConnections.BeginRead(connection);
        HoldReceipt(reference, connection, transaction);
        transaction.Commit();
    }

    internal void HoldReceipt(BlobReference reference, SqliteConnection connection, SqliteTransaction transaction)
    {
        var receipt = new NotificationReceiptCodec(instance, database, contracts).ReadMetadata(reference, connection, transaction);
        HoldJournal(receipt.Document.Receipt.JournalId);
    }

    internal void HoldDelivery(Guid operation)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records | DatabaseDomain.Notifications);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT a.journal_id FROM notifications.notification_deliveries d " +
            "JOIN records.attempt_receipts a ON a.attempt_id=d.source_attempt_id WHERE d.operation_id=$operation;";
        command.Parameters.AddWithValue("$operation", operation.ToString("D"));
        if (command.ExecuteScalar() is string journal) HoldJournal(Guid.Parse(journal));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var item in _metadata.Values) item.Lease.Dispose();
        _metadata.Clear(); _recent.Clear();
        for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
        _held.Clear(); _seen.Clear();
    }
}
