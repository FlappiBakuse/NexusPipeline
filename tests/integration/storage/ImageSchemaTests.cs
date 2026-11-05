using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ImageSchemaTests
{
    [Fact(DisplayName = "Current Image Catalog Reopens Under Its Owner And Preserves Original Pixels")]
    public async Task ReadsCurrentImages()
    {
        var fixture = new PcStageFixture(hooks: false, nativeWindow: true);
        RuntimeInstance instance = null!; string root = null!;
        ImageCatalogItem original = null!;
        await fixture.ExecuteAsync(async () =>
        {
            var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            instance = fixture.Source.Instance; root = fixture.Source.Root;
            new ImageCatalogProjection(instance, fixture.Source.Database, new(), fixture.Source.Lease).OnCommitted(receipt);
            original = Assert.Single(new ImageCatalogReader(instance, fixture.Source.Database, new())
                .ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId)).Items);
            Assert.Equal(RunImageOutcome.Captured, original.Outcome);
        });
        var path = Path.Combine(instance.RuntimeRoot, "state", "nexus.db");
        using var owner = InstanceLease.Acquire(instance, new());
        Assert.Throws<IOException>(() => InstanceLease.Acquire(instance, new()));
        var database = StateDatabase.OpenExisting(instance, Path.Combine(root, "owned-image-inspection"),
            new JournalStartupAdmission(instance, new()), owner);
        var saved = Assert.Single(new ImageCatalogReader(instance, database, new())
            .ReadPage(new(original.Key.RunId, original.Key.AttemptId)).Items);
        Assert.Equal(original, saved);
        using (var png = new ImageCatalogReader(instance, database, new())
            .OpenOriginal(saved.Key, saved.ResultSha256))
            Assert.Equal(saved.ImageSha256, Convert.ToHexStringLower(SHA256.HashData(png)));
        Assert.Equal(StateDatabase.SchemaDigest,
            DatabaseInspector.Inspect(path, instance.InstanceId, Path.Combine(root, "migrated-image-inspection")).SchemaDigest);
        _ = StateDatabase.OpenExisting(instance, Path.Combine(root, "reopened-image-inspection"));
    }

    [Fact(DisplayName = "The Current Catalog Preserves A Real Fifty Two Mi B Log")]
    public async Task PreservesLongLogs()
    {
        RuntimeInstance instance; string root; RunLogKey key; string snapshot;
        long length; string digest;
        await using (var fixture = new FileLogFixture("long-stdout"))
        {
            fixture.Prepare(); await fixture.Start(); await fixture.WaitExit();
            _ = await fixture.Collect(); await fixture.Complete();
            instance = fixture.Source.Instance; root = fixture.Source.Root;
            var receipt = new AttemptReceiptStore(fixture.Source.Database).Read(fixture.Binding.AttemptId)!;
            new LogCatalogProjection(instance, fixture.Source.Database, new(), fixture.Source.Lease).OnCommitted(receipt);
            key = new(fixture.Binding.RunId, fixture.Binding.AttemptId, fixture.Binding.StepId);
            var reader = new ExecutionLogReader(instance, fixture.Source.Database, new());
            var description = reader.Describe(key); snapshot = description.SnapshotSha256;
            Assert.InRange(description.RecordedBytes, 52L * 1024 * 1024, 64L * 1024 * 1024);
            using var original = reader.OpenOriginal(key, snapshot);
            length = original.Length; digest = Convert.ToHexStringLower(SHA256.HashData(original));
        }
        var path = Path.Combine(instance.RuntimeRoot, "state", "nexus.db");
        using var owner = InstanceLease.Acquire(instance, new());
        var database = StateDatabase.OpenExisting(instance, Path.Combine(root, "long-log-image-inspection"),
            new JournalStartupAdmission(instance, new()), owner);
        var migrated = new ExecutionLogReader(instance, database, new());
        Assert.Equal(snapshot, migrated.Describe(key).SnapshotSha256);
        using var stream = migrated.OpenOriginal(key, snapshot);
        Assert.Equal(length, stream.Length);
        Assert.Equal(digest, Convert.ToHexStringLower(SHA256.HashData(stream)));
    }

    [Fact(DisplayName = "Unknown Image Catalog Is Rejected Without Altering Its Database Group")]
    public void RejectsUnknownCatalog()
    {
        RuntimeInstance instance; string root;
        using (var fixture = new PlanBuilderFixture())
        { instance = fixture.Instance; root = fixture.Root; }
        var path = Path.Combine(instance.RuntimeRoot, "state", "nexus.db");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False;Foreign Keys=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE fixture_unknown_image_catalog(value INTEGER);";
            command.ExecuteNonQuery();
        }
        var before = GroupHashes(path);
        using var owner = InstanceLease.Acquire(instance, new());
        Assert.Equal("unsupported_database_catalog", Assert.Throws<IOException>(() =>
            StateDatabase.OpenExisting(instance, Path.Combine(root, "unknown-image-inspection"),
                new JournalStartupAdmission(instance, new()), owner)).Message);
        Assert.Equal(before, GroupHashes(path));
    }

    internal static void RevertImageTable(string path, string schema, string digest)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False;Foreign Keys=False"); connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var start = schema.IndexOf("CREATE TABLE execution_image_entries(", StringComparison.Ordinal);
        var end = start < 0 ? -1 : schema.IndexOf(';', start);
        Assert.True(start >= 0 && end > start);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DROP INDEX execution_image_entries_png;" +
            " ALTER TABLE execution_image_entries RENAME TO fixture_new_image_entries;" +
            schema[start..(end + 1)] +
            " INSERT INTO execution_image_entries SELECT * FROM fixture_new_image_entries;" +
            " DROP TABLE fixture_new_image_entries;" +
            " CREATE INDEX execution_image_entries_png ON execution_image_entries(png_sha256,attempt_id) WHERE png_sha256 IS NOT NULL;";
        command.ExecuteNonQuery();
        StateSchemaFixture.RevertTables(connection, transaction, schema);
        command.CommandText = "UPDATE schema_info SET contract_digest=$digest WHERE singleton=1;";
        command.Parameters.AddWithValue("$digest", digest);
        Assert.Equal(1, command.ExecuteNonQuery());
        transaction.Commit();
        Assert.Equal(digest, DatabaseSchema.VerifyCatalog(connection));
    }

    private static string[] GroupHashes(string path) => new[] { "", "-wal", "-shm", "-journal" }
        .Where(suffix => File.Exists(path + suffix)).Select(suffix =>
        {
            using var file = File.OpenRead(path + suffix);
            return suffix + ":" + Convert.ToHexStringLower(SHA256.HashData(file));
        }).ToArray();
}
