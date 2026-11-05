using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal sealed record TaskTestAcceptance(BlobReference Source, BlobReference Delivery, bool IsNew);

[SupportedOSPlatform("windows")]
internal sealed class TaskTestService
{
    internal static ImmutableArray<ResourceClaim> AdmissionClaims { get; } =
        [new("notifications:queue", ResourceClaimMode.Mutation), .. NotificationSender.Claims];
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly InstanceLease _lease;

    internal TaskTestService(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease)
    {
        lease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot ||
            !StringComparer.OrdinalIgnoreCase.Equals(database.DatabasePath, Path.Combine(instance.RuntimeRoot, "state", "nexus.db")))
            throw TaskTestCodec.Invalid();
        _instance = instance; _database = database; _contracts = contracts; _lease = lease;
    }

    internal TaskTestAcceptance EnqueueOwned(ResourceOperationLease actualAdmissionRoot,
        ResourceLeaseManager expectedOwner, AcceptedNotificationQueue acceptedQueue, Guid operation, string subject,
        long expectedCatalogRevision, Guid taskId, long expectedTaskRevision)
    {
        _lease.VerifyHeld(_instance);
        var identity = TaskTestCodec.Identity(operation, subject, expectedCatalogRevision, taskId, expectedTaskRevision);
        using var borrow = actualAdmissionRoot.Borrow(expectedOwner, operation, AdmissionClaims);
        using var activityProof = expectedOwner.TryContinueQueue(acceptedQueue, operation).Lease ??
            throw new IOException("notification_queue_unavailable");
        borrow.VerifyHeld();
        var existing = _lease.PublishExecution(() => ReadExisting(identity));
        if (existing is not null) return existing;
        using var prepared = PreparedTaskTest.Capture(_instance, _database, _contracts,
            operation, subject, expectedCatalogRevision, taskId, expectedTaskRevision);
        return borrow.Use(() => _lease.PublishExecution(() =>
        {
            borrow.VerifyHeld(); _lease.VerifyHeld(_instance);
            if (!acceptedQueue.IsActive) throw new IOException("notification_queue_unavailable");
            var replay = ReadExisting(identity);
            if (replay is not null) return replay;
            _database.Write((connection, transaction) =>
            {
                if (TaskTestRows.Lookup(connection, transaction, identity) is not null)
                    throw new IOException("notification_saved_test_changed");
                prepared.AcceptInside(connection, transaction, DateTimeOffset.UtcNow, _lease.StartupGeneration);
                return true;
            });
            return new TaskTestAcceptance(prepared.Source.Reference, prepared.Delivery.Reference, true);
        }));
    }

    internal TaskTestAcceptance? ReadExisting(OperationIdentity identity)
    {
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var reference = TaskTestRows.Lookup(connection, transaction, identity);
        if (reference is null) return null;
        var source = new TaskTestCodec(_instance, _database, _contracts).Read(reference, connection, transaction);
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT relative_path,size_bytes,sha256 FROM notification_deliveries WHERE operation_id=$0;",
            TaskTestCodec.DeliveryId(source.Document).ToString("D"));
        BlobReference delivery;
        using (var row = command.ExecuteReader())
        {
            if (!row.Read()) throw TaskTestCodec.Invalid();
            delivery = new(row.GetString(0), row.GetInt64(1), row.GetString(2));
        }
        _ = new NotificationDeliveryEvidence(_instance, _database, _contracts).ReadFromSnapshot(delivery, connection, transaction);
        transaction.Commit(); return new(reference, delivery, false);
    }
}
