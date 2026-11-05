using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class ExtensionSchemaTests
{
    [Theory(DisplayName = "Historical Catalogs Are Rejected With And Without An Owner Without Changing Original Bytes")]
    [InlineData("Previous")]
    [InlineData("Prior")]
    [InlineData("Automation")]
    [InlineData("Settings")]
    [InlineData("Data")]
    [InlineData("Document")]
    [InlineData("Image")]
    [InlineData("Secret")]
    [InlineData("Manual")]
    [InlineData("Effect")]
    [InlineData("Rotation")]
    [InlineData("Notification")]
    [InlineData("Epoch")]
    public void RejectsHistoricalCatalog(string stage)
    {
        LegacySchemaChecks.Reject(stage);
    }

    [Fact(DisplayName = "Invalid Saved Plugin Json Is Refused Without Initializing Or Replacing Its Original Bytes")]
    public void PreservesUnknownDocuments()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "extension-unknown-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(root); var database = StateDatabase.CreateFresh(instance);
        var bytes = "{\"existing\":1}"u8.ToArray(); var digest = ExtensionDocumentRows.Digest(bytes);
        var operation = ExtensionDocumentRows.Identity(Guid.NewGuid(), "subject", "fixture", "settings", 0, digest);
        database.Write((connection, transaction) => ExtensionDocumentRows.Write(connection, transaction, operation, "fixture", "settings", 0, bytes, digest));
        using var current = DatabaseConnections.Open(database.DatabasePath, readOnly: false);
        using var command = current.CreateCommand();
        var unknown = "{\"duplicate\":1,\"duplicate\":2}"u8.ToArray();
        command.CommandText = "UPDATE extension_documents SET content=$content,sha256=$sha WHERE extension_id='fixture';";
        command.Parameters.AddWithValue("$content", unknown); command.Parameters.AddWithValue("$sha", ExtensionDocumentRows.Digest(unknown));
        command.ExecuteNonQuery();
        Assert.Throws<System.Text.Json.JsonException>(() => ExtensionDocumentRows.Read(current, null, "fixture", "settings"));
        command.CommandText = "SELECT content FROM extension_documents WHERE extension_id='fixture';";
        Assert.Equal(unknown, Assert.IsType<byte[]>(command.ExecuteScalar()));
        Assert.Throws<System.Text.Json.JsonException>(() => database.Write((connection, transaction) => ExtensionDocumentRows.Write(connection, transaction,
            ExtensionDocumentRows.Identity(Guid.NewGuid(), "subject", "fixture", "settings", 1, digest), "fixture", "settings", 1, bytes, digest)));
        Assert.Equal(unknown, Assert.IsType<byte[]>(command.ExecuteScalar()));
    }
}
