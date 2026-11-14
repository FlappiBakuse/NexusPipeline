using System.Runtime.Versioning;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

[SupportedOSPlatform("windows")]
public sealed class NotificationDeliveryService
{
    internal const string PendingRoutesSql = "SELECT s.relative_path,s.size_bytes,s.sha256,p.next_ordinal,p.route_count " +
        "FROM notifications.notification_receipt_progress p INDEXED BY ix_notification_receipt_pending " +
        "JOIN notifications.notification_receipt_sources s ON s.operation_id=p.operation_id " +
        "WHERE p.next_ordinal<p.route_count ORDER BY p.operation_id LIMIT $0;";
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly InstanceLease _lease;

    public NotificationDeliveryService(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease)
    {
        lease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot ||
            !StringComparer.OrdinalIgnoreCase.Equals(database.DatabasePath, Path.Combine(instance.RuntimeRoot, "state", "runtime.db")))
            throw NotificationReceiptCodec.Invalid();
        _instance = instance; _database = database; _contracts = contracts; _lease = lease;
    }

    public int MaterializePending(int maximumRoutes = 64)
    {
        using var reads = new NotificationAttemptReads(_instance, _database, _contracts, _lease);
        return MaterializePending(maximumRoutes, reads);
    }

    internal int MaterializePending(int maximumRoutes, NotificationAttemptReads reads)
    {
        _lease.VerifyHeld(_instance);
        if (maximumRoutes is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(maximumRoutes));
        var pending = new List<(BlobReference Source, int Ordinal)>();
        using (var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records | DatabaseDomain.Notifications | DatabaseDomain.System | DatabaseDomain.Content))
        using (var command = NotificationDeliveryRows.Command(connection, null,
            PendingRoutesSql, maximumRoutes))
        using (var rows = command.ExecuteReader())
            while (rows.Read() && pending.Count < maximumRoutes)
            {
                var next = rows.GetInt32(3); var count = rows.GetInt32(4);
                if (next < 0 || next >= count || count > 4096) throw NotificationReceiptCodec.Invalid();
                var source = new BlobReference(rows.GetString(0), rows.GetInt64(1), rows.GetString(2));
                for (var ordinal = next; ordinal < count && pending.Count < maximumRoutes; ordinal++)
                    pending.Add((source, ordinal));
            }
        foreach (var item in pending)
        {
            reads.HoldReceipt(item.Source);
            Materialize(item.Source, item.Ordinal);
        }
        return pending.Count;
    }

    public RunDeliveryStatus ReadPendingForRun(Guid runId)
    {
        if (runId == Guid.Empty) throw new ArgumentException("notification_run_invalid", nameof(runId));
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records | DatabaseDomain.Notifications | DatabaseDomain.System);
        using var transaction = connection.BeginTransaction(deferred: true);
        BlobReference reference;
        using (var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT relative_path,size_bytes,sha256 FROM notifications.notification_receipt_sources WHERE run_id=$0;", runId.ToString("D")))
        using (var row = command.ExecuteReader())
        {
            if (!row.Read()) return new(false, false, 0, 0, 0, 0, 0, 0);
            reference = new(row.GetString(0), row.GetInt64(1), row.GetString(2));
        }
        var source = new NotificationReceiptCodec(_instance, _database, _contracts).ReadContext(reference, connection, transaction);
        var counts = new int[Enum.GetValues<NotificationDeliveryState>().Length];
        var expanded = true;
        for (var ordinal = 0; ordinal < source.Routes.Length; ordinal++)
        {
            var saved = ReceiptRouteRows.Find(connection, transaction, source.Snapshot.Document.OperationId, ordinal);
            if (saved is null) { expanded = false; continue; }
            var route = new ReceiptRouteCodec(_instance, _database, _contracts).Read(saved, source, connection, transaction);
            foreach (var target in route.Targets)
            {
                if (target.Delivery is null) { counts[(int)NotificationDeliveryState.Skipped]++; continue; }
                var delivery = new NotificationDeliveryEvidence(_instance, _database, _contracts).ReadFromSnapshot(target.Delivery, connection, transaction);
                var state = NotificationDeliveryRows.ReadState(connection, transaction, delivery.Document.OperationId);
                counts[(int)state.State]++;
            }
        }
        transaction.Commit();
        return new(true, expanded, counts[0], counts[1], counts[2], counts[3], counts[4], counts[5]);
    }

    public bool IsSettledForRun(Guid runId) => ReadPendingForRun(runId).IsSettled;

    private void Materialize(BlobReference reference, int ordinal)
    {
        _lease.VerifyHeld(_instance);
        ReceiptReadContext source;
        using (var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records | DatabaseDomain.Notifications | DatabaseDomain.System | DatabaseDomain.Content))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            source = new NotificationReceiptCodec(_instance, _database, _contracts).ReadContext(reference, connection, transaction);
            transaction.Commit();
        }
        var held = new List<IDisposable>();
        try
        {
            var blobs = new ImmutableBlobStore(_instance, _contracts);
            held.Add(blobs.OpenVerified(reference)); held.Add(blobs.OpenVerified(source.Snapshot.Document.Catalog));
            var codec = new ReceiptRouteCodec(_instance, _database, _contracts);
            var prepared = codec.Prepare(source, ordinal);
            held.Add(blobs.OpenVerified(prepared.Reference));
            foreach (var delivery in prepared.Deliveries) held.Add(blobs.OpenVerified(delivery.Reference));
            _lease.PublishExecution(() => _database.Write((connection, transaction) =>
            {
                ReceiptRouteRows.PublishInside(connection, transaction, source.Snapshot, prepared.Document,
                    prepared.Reference, prepared.Deliveries, DateTimeOffset.UtcNow, _lease.StartupGeneration);
                return true;
            }));
        }
        finally { foreach (var item in held) item.Dispose(); }
    }
}
