namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class SecretDocumentSchema
{
    internal const string Sql = """

        CREATE TABLE secret_document_publications (
            operation_id TEXT PRIMARY KEY REFERENCES operations(operation_id),
            extension_id TEXT NOT NULL,
            document_name TEXT NOT NULL,
            relative_path TEXT NOT NULL,
            size_bytes INTEGER NOT NULL CHECK(size_bytes BETWEEN 1 AND 1048576),
            sha256 TEXT NOT NULL CHECK(length(sha256)=64)
        ) STRICT;
        CREATE TABLE secret_document_fields (
            parent_operation_id TEXT NOT NULL REFERENCES secret_document_publications(operation_id),
            child_operation_id TEXT NOT NULL UNIQUE,
            subject_id TEXT NOT NULL,
            field TEXT NOT NULL,
            PRIMARY KEY(parent_operation_id,subject_id,field)
        ) STRICT;

        """;
}
