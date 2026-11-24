using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal readonly record struct NotificationProjectionChanges(long RuntimeVersion, long NotificationsVersion, bool Incremental)
{
    private const int ChangeBudget = 8192;
    private const string Kinds = "('notification.delivery','notification.registration','notification.saved-test'," +
        "'notification.scheduled-source','notification.manual-source')";
    private static readonly (string Table, string[] Keys)[] Tables =
    [
        ("notification_deliveries", ["operation_id"]),
        ("notification_delivery_secrets", ["operation_id"]),
        ("notification_delivery_assets", ["operation_id"]),
        ("notification_delivery_transitions", ["operation_id"]),
        ("notification_delivery_pending", ["operation_id"]),
        ("notification_receipt_sources", ["operation_id"]),
        ("notification_receipt_progress", ["operation_id"]),
        ("notification_receipt_secrets", ["operation_id"]),
        ("notification_receipt_routes", ["operation_id"]),
        ("notification_receipt_deliveries", ["operation_id", "delivery_operation_id"]),
        ("notification_saved_tests", ["operation_id", "delivery_operation_id"]),
        ("notification_scheduled_sources", ["operation_id"]),
        ("notification_scheduled_deliveries", ["operation_id", "delivery_operation_id"]),
        ("notification_manual_sources", ["operation_id"]),
        ("notification_manual_deliveries", ["operation_id", "delivery_operation_id"]),
    ];

    internal static NotificationProjectionChanges ReadInside(SqliteConnection connection, SqliteTransaction transaction)
    {
        var runtime = ReadVersion(connection, transaction, "main");
        var notifications = ReadVersion(connection, transaction, "notifications");
        using (var configured = DatabaseConnections.Command(connection, transaction,
            "SELECT 1 FROM temp.sqlite_schema WHERE type='table' AND name='notification_projection_version';"))
            if (configured.ExecuteScalar() is null) Configure(connection, transaction);
        // data_version is local to the pinned SQLite handle. Triggers collect its own writes;
        // another handle's commit invalidates the full proof and the collected changes.
        using var verified = DatabaseConnections.Command(connection, transaction, """
            SELECT 1 FROM temp.notification_projection_version
            WHERE runtime_version=$0 AND notifications_version=$1 AND remaining>0;
            """, runtime, notifications);
        return new(runtime, notifications, verified.ExecuteScalar() is not null);
    }

    internal void RecordVerifiedInside(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var save = DatabaseConnections.Command(connection, transaction, """
            DELETE FROM temp.notification_projection_changed;
            DELETE FROM temp.notification_projection_version;
            INSERT INTO temp.notification_projection_version VALUES($0,$1,$2);
            """, RuntimeVersion, NotificationsVersion, ChangeBudget);
        save.ExecuteNonQuery();
    }

    internal static string Rows(string table, string alias, bool changedOnly, string? relatedKey = null)
    {
        if (!changedOnly) return $"{table} {alias}";
        if (relatedKey is null) return ChangedRows(table, alias, "operation_id");
        return $"(SELECT related.* FROM {ChangedRows(table, "related", "operation_id")} UNION ALL " +
            $"SELECT related.* FROM {ChangedRows(table, "related", relatedKey)}) {alias}";
    }

    private static string ChangedRows(string table, string alias, string key) =>
        $"temp.notification_projection_changed changed CROSS JOIN {table} {alias} ON {alias}.{key}=changed.operation_id";

    private static long ReadVersion(SqliteConnection connection, SqliteTransaction transaction, string schema)
    {
        using var command = DatabaseConnections.Command(connection, transaction, $"PRAGMA {schema}.data_version;");
        return (long)command.ExecuteScalar()!;
    }

    private static void Configure(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var configure = DatabaseConnections.Command(connection, transaction, """
            CREATE TEMP TABLE notification_projection_changed(operation_id TEXT PRIMARY KEY) STRICT, WITHOUT ROWID;
            CREATE TEMP TABLE notification_projection_version(runtime_version INTEGER NOT NULL,
                notifications_version INTEGER NOT NULL, remaining INTEGER NOT NULL) STRICT;
            """);
        configure.ExecuteNonQuery();
        Track(connection, transaction, "main", "operations", ["operation_id"], operations: true);
        foreach (var (table, keys) in Tables) Track(connection, transaction, "notifications", table, keys, operations: false);
    }

    private static void Track(SqliteConnection connection, SqliteTransaction transaction, string schema, string table,
        string[] keys, bool operations)
    {
        const string tracking = "EXISTS(SELECT 1 FROM notification_projection_version WHERE remaining>0)";
        const string consume = "UPDATE notification_projection_version SET remaining=remaining-1; " +
            "DELETE FROM notification_projection_changed WHERE (SELECT remaining FROM notification_projection_version)=0;";
        var inserted = Mark("NEW", keys);
        var deleted = Mark("OLD", keys);
        var insertKind = operations ? $"AND NEW.operation_kind IN {Kinds}" : "";
        var deleteKind = operations ? $"AND OLD.operation_kind IN {Kinds}" : "";
        var updateKind = operations ? $"AND (OLD.operation_kind IN {Kinds} OR NEW.operation_kind IN {Kinds})" : "";
        using var triggers = DatabaseConnections.Command(connection, transaction, $"""
            CREATE TEMP TRIGGER projection_insert_{table} AFTER INSERT ON {schema}.{table}
            WHEN {tracking} {insertKind} BEGIN {inserted} {consume} END;
            CREATE TEMP TRIGGER projection_update_{table} AFTER UPDATE ON {schema}.{table}
            WHEN {tracking} {updateKind} BEGIN {deleted} {inserted} {consume} END;
            CREATE TEMP TRIGGER projection_delete_{table} AFTER DELETE ON {schema}.{table}
            WHEN {tracking} {deleteKind} BEGIN {deleted} {consume} END;
            """);
        triggers.ExecuteNonQuery();
    }

    private static string Mark(string row, string[] keys) => string.Join(' ', keys.Select(key => $"""
        INSERT INTO notification_projection_changed SELECT {row}.{key}
        WHERE {row}.{key} IS NOT NULL AND NOT EXISTS (
            SELECT 1 FROM notification_projection_changed WHERE operation_id={row}.{key});
        """));
}
