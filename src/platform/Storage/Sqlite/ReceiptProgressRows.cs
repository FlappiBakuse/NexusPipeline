using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Storage.Notifications;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class ReceiptProgressRows
{
    internal static void RegisterInside(SqliteConnection connection, SqliteTransaction transaction,
        Guid operationId, int routeCount)
    {
        using var insert = NotificationDeliveryRows.Command(connection, transaction,
            "INSERT INTO notification_receipt_progress(operation_id,next_ordinal,route_count) VALUES($0,0,$1);",
            operationId.ToString("D"), routeCount);
        if (insert.ExecuteNonQuery() != 1) throw NotificationReceiptCodec.Invalid();
    }

    internal static int Require(SqliteConnection connection, SqliteTransaction transaction, Guid operationId, int routeCount)
    {
        using var command = NotificationDeliveryRows.Command(connection, transaction,
            "SELECT next_ordinal,route_count,(SELECT count(*) FROM notification_receipt_routes r WHERE r.operation_id=p.operation_id)," +
            "(SELECT min(ordinal) FROM notification_receipt_routes r WHERE r.operation_id=p.operation_id)," +
            "(SELECT max(ordinal) FROM notification_receipt_routes r WHERE r.operation_id=p.operation_id) " +
            "FROM notification_receipt_progress p WHERE p.operation_id=$0;", operationId.ToString("D"));
        using var row = command.ExecuteReader();
        if (!row.Read()) throw NotificationReceiptCodec.Invalid();
        var next = row.GetInt32(0);
        if (next < 0 || next > routeCount || row.GetInt32(1) != routeCount || row.GetInt64(2) != next ||
            (next == 0 ? !row.IsDBNull(3) || !row.IsDBNull(4) : row.GetInt32(3) != 0 || row.GetInt32(4) != next - 1))
            throw NotificationReceiptCodec.Invalid();
        return next;
    }

    internal static void AdvanceInside(SqliteConnection connection, SqliteTransaction transaction,
        Guid operationId, int ordinal, int routeCount)
    {
        using var update = NotificationDeliveryRows.Command(connection, transaction,
            "UPDATE notification_receipt_progress SET next_ordinal=next_ordinal+1 " +
            "WHERE operation_id=$0 AND next_ordinal=$1 AND route_count=$2 AND next_ordinal<route_count;",
            operationId.ToString("D"), ordinal, routeCount);
        if (update.ExecuteNonQuery() != 1 || Require(connection, transaction, operationId, routeCount) != ordinal + 1)
            throw NotificationReceiptCodec.Invalid();
    }
}
