namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class ExtensionImageSchema
{
    internal const string Sql = """

        CREATE TABLE extension_images (
            extension_id TEXT NOT NULL CHECK(length(extension_id) BETWEEN 1 AND 80),
            image_id TEXT NOT NULL CHECK(length(image_id)=64),
            content_type TEXT NOT NULL CHECK(content_type IN ('image/png','image/jpeg','image/webp')),
            content BLOB NOT NULL CHECK(length(content) BETWEEN 1 AND 8388608),
            width INTEGER NOT NULL CHECK(width BETWEEN 1 AND 8192),
            height INTEGER NOT NULL CHECK(height BETWEEN 1 AND 8192),
            PRIMARY KEY(extension_id,image_id)
        ) STRICT;
        CREATE TABLE extension_image_references (
            extension_id TEXT NOT NULL,
            document_name TEXT NOT NULL,
            image_id TEXT NOT NULL,
            PRIMARY KEY(extension_id,document_name,image_id),
            FOREIGN KEY(extension_id,document_name) REFERENCES extension_documents(extension_id,name),
            FOREIGN KEY(extension_id,image_id) REFERENCES extension_images(extension_id,image_id)
        ) STRICT;

        """;
}
