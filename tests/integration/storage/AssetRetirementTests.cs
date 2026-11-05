using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class AssetRetirementTests
{
    [Fact(DisplayName = "Deleting The Last Script Registers Its Cleanup Before Removing References And Keeps Its Original Replay Result")]
    public void RegistersBeforeRetirement()
    {
        using var fixture = AssetPreparationFixture.Create();
        fixture.Execute();
        var store = new AutomationDefinitionStore(fixture.Database);
        var changes = Remove(store.Read(), store.Read().Scripts[0].Id);
        var operation = AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes);
        var result = store.Commit(operation, changes, DateTimeOffset.UtcNow);
        var cleanupId = Assert.Single(result.CleanupJournalIds);
        Assert.Empty(store.Read().Scripts);
        Assert.Equal(0, Count(fixture.Database, "automation_assets"));
        Assert.Equal(0, Count(fixture.Database, "automation_asset_owners"));
        var journal = new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals.Single(value => value.JournalId == cleanupId);
        Assert.Equal(operation.OperationId, journal.OperationId);
        Assert.Equal(JournalKind.FileCleanup, journal.Kind);
        Assert.Equal(JournalPhase.Prepared, new JournalStore(fixture.Instance, new()).Read(cleanupId).Phase);
        Assert.Equal(FileEffectState.Pending, Assert.Single(new FileEffectOutbox(fixture.Database).Read(cleanupId)).State);
        var path = ContentPath(fixture);
        Assert.True(File.Exists(path));
        Assert.False(References(fixture).IsReferenced(Content(fixture)));
        var cleanup = new FileCleanupRecovery(new CleanupRecoverySession(fixture.Instance, cleanupId, new(), fixture.Database,
            () => new(fixture.Instance.InstanceId, cleanupId, true, WriterState.Stopped)));
        Assert.Equal(FileCleanupResult.Cleaned, cleanup.Execute());
        Assert.False(File.Exists(path));
        var replay = store.Commit(operation, changes, DateTimeOffset.UtcNow);
        Assert.False(replay.IsNew);
        Assert.Equal(result.CatalogRevision, replay.CatalogRevision);
        Assert.Equal(result.CleanupJournalIds.ToArray(), replay.CleanupJournalIds.ToArray());
        Assert.Throws<OperationConflictException>(() => store.Commit(new(operation.OperationId, "other", operation.RequestDigest, operation.Kind), changes, DateTimeOffset.UtcNow));
        var roots = new PayloadApplyRoots(Path.Combine(fixture.Root, "host"), Path.Combine(fixture.Instance.RuntimeRoot, "extensions"));
        var index = new StartupJournalRecovery(fixture.Instance, new(), fixture.Database, roots).Recover(fixture.InstanceLease);
        Assert.True(index.Current.IsReady);
        Assert.Empty(index.Current.Isolations);
    }

    [Fact(DisplayName = "Shared Bundle Content Stays Owned Until The Final Script Reference Is Removed")]
    public void KeepsSharedOwners()
    {
        using var fixture = AssetPreparationFixture.Create(scriptCount: 2);
        fixture.Execute();
        var store = new AutomationDefinitionStore(fixture.Database);
        Assert.Equal(2, Count(fixture.Database, "automation_asset_owners"));
        var first = Apply(store, Remove(store.Read(), store.Read().Scripts[0].Id));
        Assert.Empty(first.CleanupJournalIds);
        Assert.Equal(1, Count(fixture.Database, "automation_assets"));
        Assert.Equal(1, Count(fixture.Database, "automation_asset_owners"));
        Assert.True(References(fixture).IsReferenced(Content(fixture)));
        var last = Apply(store, Remove(store.Read(), store.Read().Scripts[0].Id));
        Assert.Single(last.CleanupJournalIds);
        Assert.Equal(0, Count(fixture.Database, "automation_assets"));
        Assert.False(References(fixture).IsReferenced(Content(fixture)));
        Assert.True(File.Exists(ContentPath(fixture)));
    }

    [Fact(DisplayName = "An Actual Delete Failure Rolls Back Cleanup Registration Asset Owners And The Definition Operation Together")]
    public void RollsBackDeletion()
    {
        using var fixture = AssetPreparationFixture.Create();
        fixture.Execute();
        var store = new AutomationDefinitionStore(fixture.Database);
        var changes = Remove(store.Read(), store.Read().Scripts[0].Id);
        var operation = AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes);
        Sql(fixture.Database, "CREATE TRIGGER fixture_fail_delete BEFORE DELETE ON automations BEGIN SELECT RAISE(ABORT,'fixture_delete_failure'); END;");
        Assert.Throws<SqliteException>(() => store.Commit(operation, changes, DateTimeOffset.UtcNow));
        Assert.Single(store.Read().Scripts);
        Assert.Equal(1, Count(fixture.Database, "automation_assets"));
        Assert.Equal(1, Count(fixture.Database, "automation_asset_owners"));
        Assert.Equal(1, Count(fixture.Database, "operations"));
        Assert.Single(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals);
        Assert.True(File.Exists(ContentPath(fixture)));
        Sql(fixture.Database, "DROP TRIGGER fixture_fail_delete;");
        Assert.Single(store.Commit(operation, changes, DateTimeOffset.UtcNow).CleanupJournalIds);
    }

    [Fact(DisplayName = "A Historical Record Retains The Same Blob After Its Definition Asset Reference Is Retired")]
    public void KeepsHistoricalReferences()
    {
        using var fixture = AssetPreparationFixture.Create();
        fixture.Execute();
        var record = RecordStoreFixture.Record(1, Guid.NewGuid());
        new RunRecordStore(fixture.Database).AppendCompleted([record]);
        var content = Content(fixture);
        new RecordAssetStore(fixture.Database, new(fixture.Instance, new())).AttachVerified(
            new(record.FinalAttempt.RunId, content.Sha256, content.SizeBytes, RecordAssetKind.Screenshot));
        var store = new AutomationDefinitionStore(fixture.Database);
        var result = Apply(store, Remove(store.Read(), store.Read().Scripts[0].Id));
        var id = Assert.Single(result.CleanupJournalIds);
        var cleanup = new FileCleanupRecovery(new CleanupRecoverySession(fixture.Instance, id, new(), fixture.Database,
            () => new(fixture.Instance.InstanceId, id, true, WriterState.Stopped)));
        Assert.Equal(FileCleanupResult.Cleaned, cleanup.Execute());
        Assert.True(File.Exists(ContentPath(fixture)));
        Assert.True(References(fixture).IsReferenced(content));
        Assert.Single(new RecordAssetStore(fixture.Database, new(fixture.Instance, new())).ReadForRun(record.FinalAttempt.RunId).Items);
        Assert.Equal(1, Count(fixture.Database, "run_records"));
    }

    private static AutomationChanges Remove(AutomationCatalog catalog, Guid id) => new(catalog.Revision, [], [], [], [], [], [],
        [new(AutomationDefinitionKind.Script, id, catalog.Scripts.Single(value => value.Id == id).Revision)]);
    private static AutomationChangeResult Apply(AutomationDefinitionStore store, AutomationChanges changes) =>
        store.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes), changes, DateTimeOffset.UtcNow);
    private static BlobReference Content(AssetPreparationFixture fixture)
    {
        var content = Assert.Single(fixture.Prepared.Plan.ReadWire().Assets).Content;
        return new(content.RelativePath, content.SizeBytes, content.Sha256);
    }
    private static string ContentPath(AssetPreparationFixture fixture) => Path.Combine(fixture.Instance.RuntimeRoot, Content(fixture).RelativePath);
    private static BlobReferenceIndex References(AssetPreparationFixture fixture) => new(fixture.Instance, new(), fixture.Database, new(fixture.Instance, new()));
    private static long Count(StateDatabase database, string table) => Sql(database, "SELECT count(*) FROM " + table);
    private static long Sql(StateDatabase database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={database.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
