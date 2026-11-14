using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.StorageTests;

internal sealed class ConfigurationBatchFixture
{
    internal string Root { get; }
    internal RuntimeInstance Instance { get; }
    internal StateDatabase Database { get; }
    internal ConfigurationRevisionStore Revisions { get; }
    internal ConfigurationBatchStore Batches { get; }
    internal Guid Binding { get; }
    internal Guid OtherBinding { get; }

    internal ConfigurationBatchFixture()
    {
        Root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "config-batch-" + Guid.NewGuid().ToString("N"));
        Instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(Root, "runtime"));
        Database = StateDatabase.CreateFresh(Instance);
        var changes = AutomationDefinitionSeed.Create(Root);
        var definitions = new AutomationDefinitionStore(Database);
        definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes), changes, DateTimeOffset.UtcNow);
        Binding = changes.Bindings[0].Id; OtherBinding = changes.Bindings[1].Id;
        Revisions = new(Instance, Database, new()); Batches = new(Instance, Database, new());
    }

    internal ConfigurationRevisionChange Capture(string resource, string text, long expectedRevision = 0)
    {
        var path = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, text);
        return CaptureTree(resource, path, expectedRevision);
    }

    internal ConfigurationRevisionChange CaptureTree(string resource, string path, long expectedRevision = 0)
    {
        var scanner = new FileTreeScanner(SnapshotBudget.Default);
        var tree = new FileTreeArchive(scanner, new(Instance, new())).Capture(path, default);
        return new(new(Binding, resource), expectedRevision, 1, new(1, new('a', 64), null, false), tree.Snapshot, ConfigurationUpdateKind.Manual);
    }

    internal ConfigurationBatchResult Save(ConfigurationRevisionBatch batch) =>
        Save(ConfigurationBatchStore.Identity(Guid.NewGuid(), "fixture", batch), batch);

    internal ConfigurationBatchResult Save(NexusPipeline.Core.Operations.OperationIdentity operation, ConfigurationRevisionBatch batch)
    {
        using var prepared = Batches.Prepare(batch);
        return Batches.Commit(operation, prepared, DateTimeOffset.UtcNow);
    }

    internal SqliteConnection Connect()
    {
        var connection = NexusPipeline.TestFixtures.DatabaseFixtureConnection.Open(new SqliteConnectionStringBuilder { DataSource = Database.DatabasePath, Pooling = false }.ToString());
        connection.Open(); return connection;
    }

    internal void Execute(string sql)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }

    internal long Count(string table)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand(); command.CommandText = $"SELECT count(*) FROM {table};"; return (long)command.ExecuteScalar()!;
    }
}
