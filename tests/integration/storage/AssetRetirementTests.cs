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
        var catalog = store.Read();
        var changes = Remove(catalog, Assert.Single(catalog.Scripts).Id);
        var content = Content(fixture);
        var path = ContentPath(fixture);
        var identity = PhysicalObjectIdentity.Read(path);
        var bytes = File.ReadAllBytes(path);
        var operation = AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes);
        var result = store.Commit(operation, changes, DateTimeOffset.UtcNow);
        var cleanupId = Assert.Single(result.CleanupJournalIds);
        Assert.Empty(store.Read().Scripts);
        var journal = new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals.Single(value => value.JournalId == cleanupId);
        Assert.Equal(operation.OperationId, journal.OperationId);
        var item = Assert.Single(CleanupJournalBinding.ReadPlan(fixture.Instance, new(), journal, fixture.Database).Items);
        Assert.Equal(content, item.Blob);
        Assert.Equal(identity, item.Identity);
        Assert.Equal(identity, PhysicalObjectIdentity.Read(path));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        var cleanup = new FileCleanupRecovery(new CleanupRecoverySession(fixture.Instance, cleanupId, new(), fixture.Database,
            () => new(fixture.Instance.InstanceId, cleanupId, true, WriterState.Stopped)));
        Assert.Equal(FileCleanupResult.Cleaned, cleanup.Execute());
        Assert.False(Path.Exists(path));
        var roots = new PayloadApplyRoots(Path.Combine(fixture.Root, "host"), Path.Combine(fixture.Instance.RuntimeRoot, "extensions"));
        var index = new StartupJournalRecovery(fixture.Instance, new(), fixture.Database, roots).Recover(fixture.InstanceLease);
        Assert.True(index.Current.IsReady);
        Assert.Empty(index.Current.Isolations);
        var replay = store.Commit(operation, changes, DateTimeOffset.UtcNow);
        Assert.False(replay.IsNew);
        Assert.Equal(result.CatalogRevision, replay.CatalogRevision);
        Assert.Equal(result.CleanupJournalIds.ToArray(), replay.CleanupJournalIds.ToArray());
        Assert.Throws<OperationConflictException>(() => store.Commit(new(operation.OperationId, "other", operation.RequestDigest, operation.Kind), changes, DateTimeOffset.UtcNow));
    }

    [Fact(DisplayName = "Shared Bundle Content Stays Owned Until The Final Script Reference Is Removed")]
    public void KeepsSharedOwners()
    {
        using var fixture = AssetPreparationFixture.Create(scriptCount: 2);
        fixture.Execute();
        var store = new AutomationDefinitionStore(fixture.Database);
        var initial = store.Read();
        var removed = initial.Scripts[0].Id;
        var retained = initial.Scripts.Single(script => script.Id != removed);
        var path = ContentPath(fixture);
        var bytes = File.ReadAllBytes(path);
        var identity = PhysicalObjectIdentity.Read(path);
        var first = Apply(store, Remove(initial, removed));
        Assert.Empty(first.CleanupJournalIds);
        var afterFirst = store.Read();
        Assert.Equal(retained.Id, Assert.Single(afterFirst.Scripts).Id);
        Assert.True(References(fixture).IsReferenced(Content(fixture)));
        var last = Apply(store, Remove(afterFirst, retained.Id));
        Assert.Single(last.CleanupJournalIds);
        Assert.False(References(fixture).IsReferenced(Content(fixture)));
        Assert.Equal(identity, PhysicalObjectIdentity.Read(path));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact(DisplayName = "An Actual Delete Failure Rolls Back Cleanup Registration Asset Owners And The Definition Operation Together")]
    public void RollsBackDeletion()
    {
        using var fixture = AssetPreparationFixture.Create();
        fixture.Execute();
        var store = new AutomationDefinitionStore(fixture.Database);
        var before = store.Read();
        var original = Assert.Single(before.Scripts);
        var changes = Remove(before, original.Id);
        var operation = AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes);
        var path = ContentPath(fixture);
        var identity = PhysicalObjectIdentity.Read(path);
        var bytes = File.ReadAllBytes(path);
        Sql(fixture.Database, "CREATE TRIGGER automation.fixture_fail_delete BEFORE DELETE ON automations BEGIN SELECT RAISE(ABORT,'fixture_delete_failure'); END;");
        Assert.Contains("fixture_delete_failure", Assert.Throws<SqliteException>(() =>
            store.Commit(operation, changes, DateTimeOffset.UtcNow)).Message);
        var after = store.Read();
        var retained = Assert.Single(after.Scripts);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal((original.Id, original.Revision), (retained.Id, retained.Revision));
        Assert.True(References(fixture).IsReferenced(Content(fixture)));
        var registry = new RecoveryJournalRegistry(fixture.Database);
        Assert.DoesNotContain(registry.ReadSnapshot().Journals, item => item.OperationId == operation.OperationId);
        Assert.Equal(identity, PhysicalObjectIdentity.Read(path));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Sql(fixture.Database, "DROP TRIGGER automation.fixture_fail_delete;");
        var retried = store.Commit(operation, changes, DateTimeOffset.UtcNow);
        Assert.True(retried.IsNew);
        Assert.Single(retried.CleanupJournalIds);
    }

    [Fact(DisplayName = "A Historical Record Retains The Same Blob After Its Definition Asset Reference Is Retired")]
    public void KeepsHistoricalReferences()
    {
        using var fixture = AssetPreparationFixture.Create();
        fixture.Execute();
        var record = RecordStoreFixture.Record(1, Guid.NewGuid());
        var records = new RunRecordStore(fixture.Database);
        records.AppendCompleted([record]);
        var content = Content(fixture);
        var asset = new RecordAsset(record.FinalAttempt.RunId, content.Sha256, content.SizeBytes, RecordAssetKind.Screenshot);
        var assets = new RecordAssetStore(fixture.Database, new(fixture.Instance, new()));
        assets.AttachVerified(asset);
        var path = ContentPath(fixture);
        var identity = PhysicalObjectIdentity.Read(path);
        var bytes = File.ReadAllBytes(path);
        var store = new AutomationDefinitionStore(fixture.Database);
        var catalog = store.Read();
        var result = Apply(store, Remove(catalog, Assert.Single(catalog.Scripts).Id));
        var id = Assert.Single(result.CleanupJournalIds);
        var cleanup = new FileCleanupRecovery(new CleanupRecoverySession(fixture.Instance, id, new(), fixture.Database,
            () => new(fixture.Instance.InstanceId, id, true, WriterState.Stopped)));
        Assert.Equal(FileCleanupResult.Cleaned, cleanup.Execute());
        Assert.Equal(identity, PhysicalObjectIdentity.Read(path));
        Assert.Equal(record, Assert.Single(records.ReadPage(new()).Items));
        Assert.Equal(asset, Assert.Single(assets.ReadForRun(asset.RunId).Items));
        using var stream = assets.OpenVerified(asset);
        var actual = new byte[bytes.Length];
        stream.ReadExactly(actual);
        Assert.Equal(bytes, actual);
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
    private static void Sql(StateDatabase database, string sql)
    {
        using var connection = NexusPipeline.TestFixtures.DatabaseFixtureConnection.Open($"Data Source={database.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
