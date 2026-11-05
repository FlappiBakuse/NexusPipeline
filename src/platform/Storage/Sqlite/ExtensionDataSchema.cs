namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class ExtensionDataSchema
{
    internal const string Sql = """

        CREATE TABLE extension_documents (
            extension_id TEXT NOT NULL CHECK(length(extension_id) BETWEEN 1 AND 80),
            name TEXT NOT NULL CHECK(length(name) BETWEEN 1 AND 80),
            revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991),
            content BLOB NOT NULL CHECK(length(content) BETWEEN 1 AND 1048576),
            sha256 TEXT NOT NULL CHECK(length(sha256)=64),
            operation_id TEXT NOT NULL UNIQUE REFERENCES operations(operation_id),
            PRIMARY KEY(extension_id,name)
        ) STRICT;
        CREATE TABLE extension_document_commits (
            operation_id TEXT PRIMARY KEY NOT NULL REFERENCES operations(operation_id),
            extension_id TEXT NOT NULL,
            name TEXT NOT NULL,
            revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991),
            sha256 TEXT NOT NULL CHECK(length(sha256)=64)
        ) STRICT;

        """;
}
