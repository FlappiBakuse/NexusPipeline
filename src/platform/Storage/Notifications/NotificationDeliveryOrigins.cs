using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal static class NotificationDeliveryOrigins
{
    internal static BlobReference? Reference(NotificationDeliveryDocument document) =>
        document.Source.Kind == "executionReceipt" ? document.Registration : document.Source.ExplicitSource;

    internal static Guid? SourceOperation(NotificationDeliveryDocument document) => Reference(document) is null ? null : document.AcceptanceId;

    internal static void RequireMembership(SqliteConnection connection, SqliteTransaction transaction, NotificationDeliverySnapshot delivery)
    {
        if (delivery.Document.Source.Kind == "executionReceipt")
            ReceiptRouteRows.RequireMembership(connection, transaction, delivery);
        else if (delivery.Document.Source.Kind == "savedTaskTest")
            TaskTestRows.RequireMembership(connection, transaction, delivery);
        else if (delivery.Document.Source.Kind == "scheduledInvocation")
            NotificationScheduledRows.RequireMembership(connection, transaction, delivery);
        else if (delivery.Document.Source.Kind == "manualInvocation")
            NotificationManualRows.RequireMembership(connection, transaction, delivery);
        else throw new IOException("notification_delivery_source_invalid");
    }

    internal static void RequireRegistered(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        NotificationDeliverySnapshot delivery, SqliteConnection connection, SqliteTransaction transaction)
    {
        if (Reference(delivery.Document) is null) throw new IOException("notification_delivery_registration_required");
        if (delivery.Document.Source.Kind == "executionReceipt")
            new ReceiptRouteCodec(instance, database, contracts).RequireOrigin(delivery, connection, transaction);
        else new ExplicitSourceEvidence(instance, database, contracts).RequireDelivery(delivery, connection, transaction);
    }

    internal static Guid ReadOperation(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        BlobReference reference, SqliteConnection connection, SqliteTransaction transaction)
    {
        using var held = new ImmutableBlobStore(instance, contracts).OpenVerified(reference);
        if (held.Length is < 1 or > StrictJson.MaximumBytes) throw new IOException("notification_delivery_source_invalid");
        var bytes = new byte[checked((int)held.Length)]; held.ReadExactly(bytes);
        using var document = StrictJson.Parse(bytes);
        var format = document.RootElement.GetProperty("format").GetString();
        return format switch {
            "nexus.notification.receipt-source" => new NotificationReceiptCodec(instance, database, contracts)
                .ReadContext(reference, connection, transaction).Snapshot.Document.OperationId,
            TaskTestCodec.Format => new TaskTestCodec(instance, database, contracts)
                .Read(reference, connection, transaction).Document.OperationId,
            NotificationScheduledCodec.Format => new NotificationScheduledCodec(instance, contracts)
                .Read(reference, connection, transaction).Document.OperationId,
            NotificationManualCodec.Format => new NotificationManualCodec(instance, contracts)
                .Read(reference, connection, transaction).Document.OperationId,
            _ => throw new IOException("notification_delivery_source_invalid") };
    }
}
