using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Storage.Notifications;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class ReceiptRouteRows
{
    internal static BlobReference? Find(SqliteConnection connection, SqliteTransaction transaction, Guid source, int ordinal)
    {
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT relative_path,size_bytes,sha256 FROM notification_receipt_routes WHERE operation_id=$0 AND ordinal=$1;",
            source.ToString("D"), ordinal);
        using var row = command.ExecuteReader();
        return row.Read() ? new(row.GetString(0), row.GetInt64(1), row.GetString(2)) : null;
    }

    internal static void PublishInside(SqliteConnection connection, SqliteTransaction transaction,
        NotificationReceiptSnapshot source, ReceiptRouteDocument document, BlobReference reference,
        IReadOnlyList<NotificationDeliverySnapshot> deliveries, DateTimeOffset acceptedAt, Guid generation)
    {
        NotificationReceiptRows.RequireSaved(connection, transaction, source);
        NotificationReceiptRows.RequireReferences(connection, transaction, source);
        if (Find(connection, transaction, source.Document.OperationId, document.Ordinal) is { } existing)
        {
            if (existing != reference) throw NotificationReceiptCodec.Invalid();
            RequireSaved(connection, transaction, document, reference);
            return;
        }
        if (ReceiptProgressRows.Require(connection, transaction, source.Document.OperationId, source.Document.RouteCount) != document.Ordinal)
            throw NotificationReceiptCodec.Invalid();
        if (document.SourceOperationId != source.Document.OperationId || document.Ordinal < 0 ||
            document.Ordinal >= source.Document.RouteCount ||
            !document.Targets.Where(item => item.DeliveryOperationId is not null).Select(item => item.DeliveryOperationId!.Value)
                .SequenceEqual(deliveries.Select(item => item.Document.OperationId)))
            throw NotificationReceiptCodec.Invalid();
        using (var insert = NotificationDeliveryRows.Command(connection, transaction,
            "INSERT INTO notification_receipt_routes VALUES($0,$1,$2,$3,$4);", document.SourceOperationId.ToString("D"),
            document.Ordinal, reference.RelativePath, reference.SizeBytes, reference.Sha256)) insert.ExecuteNonQuery();
        ReceiptProgressRows.AdvanceInside(connection, transaction, document.SourceOperationId, document.Ordinal, source.Document.RouteCount);
        foreach (var delivery in deliveries)
        {
            NotificationDeliveryRows.AcceptRegisteredInside(connection, transaction, delivery, source, acceptedAt, generation);
            using var insert = NotificationDeliveryRows.Command(connection, transaction,
                "INSERT INTO notification_receipt_deliveries VALUES($0,$1,$2);", document.SourceOperationId.ToString("D"),
                document.Ordinal, delivery.Document.OperationId.ToString("D"));
            insert.ExecuteNonQuery();
        }
        RequireSaved(connection, transaction, document, reference);
    }

    internal static void RequireSaved(SqliteConnection connection, SqliteTransaction transaction,
        ReceiptRouteDocument document, BlobReference reference)
    {
        if (Find(connection, transaction, document.SourceOperationId, document.Ordinal) != reference)
            throw NotificationReceiptCodec.Invalid();
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT delivery_operation_id FROM notification_receipt_deliveries WHERE operation_id=$0 AND route_ordinal=$1 ORDER BY delivery_operation_id;",
            document.SourceOperationId.ToString("D"), document.Ordinal);
        using var rows = command.ExecuteReader();
        var expected = document.Targets.Where(item => item.DeliveryOperationId is not null)
            .Select(item => item.DeliveryOperationId!.Value.ToString("D")).Order(StringComparer.Ordinal).ToArray();
        var actual = new List<string>(); while (rows.Read()) actual.Add(rows.GetString(0));
        if (!actual.SequenceEqual(expected)) throw NotificationReceiptCodec.Invalid();
    }

    internal static void RequireMembership(SqliteConnection connection, SqliteTransaction transaction,
        NotificationDeliverySnapshot snapshot)
    {
        var document = snapshot.Document;
        if (document.Registration is null || document.RouteOrdinal is null) throw NotificationReceiptCodec.Invalid();
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT s.relative_path,s.size_bytes,s.sha256,m.route_ordinal FROM notification_receipt_deliveries m " +
            "JOIN notification_receipt_sources s ON s.operation_id=m.operation_id " +
            "WHERE m.delivery_operation_id=$0 AND m.operation_id=$1;", document.OperationId.ToString("D"), document.AcceptanceId.ToString("D"));
        using var row = command.ExecuteReader();
        if (!row.Read() || new BlobReference(row.GetString(0), row.GetInt64(1), row.GetString(2)) != document.Registration ||
            row.GetInt32(3) != document.RouteOrdinal) throw NotificationReceiptCodec.Invalid();
    }
}
