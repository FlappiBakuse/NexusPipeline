namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class NotificationManualSchema
{
    internal const string Sql = """

        CREATE TABLE notification_manual_sources (
            operation_id TEXT PRIMARY KEY REFERENCES operations(operation_id),
            invocation_operation_id TEXT NOT NULL UNIQUE REFERENCES manual_work_runs(operation_id),
            relative_path TEXT NOT NULL,
            size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 2097152),
            sha256 TEXT NOT NULL CHECK(length(sha256)=64)
        ) STRICT;
        CREATE TABLE notification_manual_deliveries (
            operation_id TEXT NOT NULL REFERENCES notification_manual_sources(operation_id),
            task_id TEXT NOT NULL,
            delivery_operation_id TEXT NOT NULL UNIQUE REFERENCES notification_deliveries(operation_id) DEFERRABLE INITIALLY DEFERRED,
            PRIMARY KEY(operation_id,task_id)
        ) STRICT;
        """;
}
