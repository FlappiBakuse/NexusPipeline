using System.Runtime.Versioning;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

[SupportedOSPlatform("windows")]
internal sealed class NotificationSendingStore(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease)
{
    internal NotificationDeliveryTransition Claim(NotificationDeliverySnapshot snapshot, NotificationDeliveryNetwork network)
    {
        var sender = NotificationDeliveryOwner.Capture(JournalWriterStore.CurrentWriter);
        return lease.PublishExecution(() =>
        {
            using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
            using (var transaction = connection.BeginTransaction(deferred: true))
            {
                _ = new NotificationDeliveryEvidence(instance, database, contracts).ReadFromSnapshot(snapshot.Reference, connection, transaction);
                NotificationDeliveryOrigins.RequireRegistered(instance, database, contracts, snapshot, connection, transaction);
                transaction.Commit();
            }
            return database.Write((connection, transaction) =>
            {
                NotificationDeliveryRows.RequireSaved(connection, transaction, snapshot);
                NotificationDeliveryOrigins.RequireMembership(connection, transaction, snapshot);
                DeliveryNetworkEvidence.RequireCurrent(connection, transaction, instance.InstanceId, network);
                var before = NotificationDeliveryRows.ReadState(connection, transaction, snapshot.Document.OperationId);
                NotificationDeliveryRows.RequirePendingProjection(connection, transaction, before, snapshot.Document.AcceptanceId);
                if (before.State != NotificationDeliveryState.Queued) throw new IOException("notification_delivery_already_claimed");
                var next = new NotificationDeliveryTransition("nexus.notification.delivery-transition", 1,
                    before.OperationId, before.Ordinal + 1, NotificationDeliveryState.Sending, lease.StartupGeneration,
                    sender, "notification_sending", null, network);
                NotificationDeliveryRows.AppendInside(connection, transaction, snapshot, next);
                return next;
            });
        });
    }

    internal void Publish(NotificationDeliverySnapshot snapshot, NotificationDeliveryTransition claimed, NotificationTransportResult outcome)
    {
        lease.VerifyHeld(instance);
        if (claimed.Sender != NotificationDeliveryOwner.Capture(JournalWriterStore.CurrentWriter) ||
            claimed.OwnerGeneration != lease.StartupGeneration || claimed.State != NotificationDeliveryState.Sending)
            throw new IOException("notification_delivery_sender_unbound");
        lease.PublishExecution(() =>
        {
            using (var read = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
            using (var snapshotRead = read.BeginTransaction(deferred: true))
            {
                _ = new NotificationDeliveryEvidence(instance, database, contracts).ReadFromSnapshot(snapshot.Reference, read, snapshotRead);
                snapshotRead.Commit();
            }
            return database.Write((connection, transaction) =>
            {
                NotificationDeliveryRows.RequireSaved(connection, transaction, snapshot);
                NotificationDeliveryOrigins.RequireMembership(connection, transaction, snapshot);
                var current = NotificationDeliveryRows.ReadState(connection, transaction, snapshot.Document.OperationId);
                var result = claimed with { Ordinal = claimed.Ordinal + 1, State = outcome.State,
                    ReasonCode = NotificationDeliveryStates.Reason(outcome.State), ProtocolStatusCode = outcome.StatusCode };
                if (current != claimed && current != result) throw new IOException("notification_delivery_sender_unbound");
                NotificationDeliveryRows.AppendInside(connection, transaction, snapshot, result);
                return true;
            });
        });
    }

    internal bool ReconcileStoppedOwner(BlobReference reference)
    {
        lease.VerifyHeld(instance);
        var snapshot = new NotificationDeliveryEvidence(instance, database, contracts).Read(reference);
        NotificationDeliveryTransition prior;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
            prior = NotificationDeliveryRows.ReadState(connection, null, snapshot.Document.OperationId);
        if (prior.State != NotificationDeliveryState.Sending || prior.Sender is null ||
            WriterStopEvidence.ObserveProcess(prior.Sender.ToProcessIdentity()) != NexusPipeline.Core.Execution.WriterState.Stopped) return false;
        lease.PublishExecution(() => database.Write((connection, transaction) =>
        {
            if (NotificationDeliveryRows.ReadState(connection, transaction, prior.OperationId) != prior)
                throw new IOException("notification_delivery_sender_unbound");
            NotificationDeliveryRows.AppendInside(connection, transaction, snapshot, prior with {
                Ordinal = prior.Ordinal + 1, State = NotificationDeliveryState.Unknown, OwnerGeneration = lease.StartupGeneration,
                ReasonCode = "notification_delivery_unknown", ProtocolStatusCode = null });
            return true;
        }));
        return true;
    }
}
