namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class ManualWorkSchema
{
    internal const string Sql = """

        CREATE TABLE manual_work_runs (
            operation_id TEXT PRIMARY KEY REFERENCES operations(operation_id),
            extension_id TEXT NOT NULL,
            provider_id TEXT NOT NULL,
            task_id TEXT NOT NULL,
            relative_path TEXT NOT NULL,
            size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 1048576),
            sha256 TEXT NOT NULL CHECK(length(sha256)=64)
        ) STRICT;
        CREATE TABLE manual_work_begin (
            operation_id TEXT PRIMARY KEY REFERENCES manual_work_runs(operation_id),
            relative_path TEXT NOT NULL,
            size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 1048576),
            sha256 TEXT NOT NULL CHECK(length(sha256)=64)
        ) STRICT;
        CREATE TABLE manual_work_result (
            operation_id TEXT PRIMARY KEY REFERENCES manual_work_runs(operation_id),
            relative_path TEXT NOT NULL,
            size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 1048576),
            sha256 TEXT NOT NULL CHECK(length(sha256)=64)
        ) STRICT;
        CREATE INDEX manual_work_tasks ON manual_work_runs(extension_id,provider_id,task_id);

        """;
}
