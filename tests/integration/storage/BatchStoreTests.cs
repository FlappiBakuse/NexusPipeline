using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class BatchStoreTests
{
    [Fact(DisplayName = "Main And Additional Configurations Publish In One Transaction With Independent Versions And Owners")]
    public void PublishesIndependentResources()
    {
        var fixture = new ConfigurationBatchFixture();
        var main = fixture.Capture("main", "first");
        var extra = fixture.Capture("extra", "second");
        var result = fixture.Save(new([main, extra]));
        Assert.True(result.IsNew);
        Assert.All(result.Items, item => Assert.True(item.IsNew));
        Assert.Equal(new[] { "extra", "main" }, result.Items.Select(item => item.Saved.Address.ResourceId));
        Assert.Equal(main.Tree.Entries, fixture.Revisions.Read(main.Address)!.Tree.Entries);
        Assert.Equal(extra.Tree.Entries, fixture.Revisions.Read(extra.Address)!.Tree.Entries);
        Assert.Null(fixture.Revisions.Read(new(fixture.OtherBinding, "main")));
        var reopened = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "diagnostics"), new JournalStartupAdmission(fixture.Instance, new()));
        var store = new ConfigurationRevisionStore(fixture.Instance, reopened, new());
        Assert.Equal(1, store.Read(main.Address)!.Revision);
        Assert.Equal(1, store.Read(extra.Address)!.Revision);
    }

    [Theory(DisplayName = "Failure After The First Resource Publication Rolls Back Every Pointer Asset Result And Operation")]
    [InlineData("configuration_batch_items", "NEW.ordinal=1")]
    [InlineData("configuration_current", "NEW.resource_id='z-main'")]
    [InlineData("configuration_revision_assets", "NEW.resource_id='z-main'")]
    public void RevertsPartialPublication(string table, string when)
    {
        var fixture = new ConfigurationBatchFixture();
        ConfigurationRevisionBatch initial = new([fixture.Capture("a-extra", "old-extra"), fixture.Capture("z-main", "old-main")]);
        fixture.Save(initial);
        ConfigurationRevisionBatch next = new([fixture.Capture("a-extra", "new-extra", 1), fixture.Capture("z-main", "new-main", 1)]);
        var identity = ConfigurationBatchStore.Identity(Guid.NewGuid(), "fixture", next);
        using var prepared = fixture.Batches.Prepare(next);
        fixture.Execute($"CREATE TRIGGER fixture_fail BEFORE INSERT ON {table} WHEN {when} BEGIN SELECT RAISE(ABORT,'fixture_failure'); END;");
        Assert.Throws<SqliteException>(() => fixture.Batches.Commit(identity, prepared, DateTimeOffset.UtcNow));
        foreach (var old in initial.Changes) Assert.Equal(old.Tree.Entries, fixture.Revisions.Read(old.Address)!.Tree.Entries);
        Assert.Equal(2, fixture.Count("configuration_revisions"));
        Assert.Equal(2, fixture.Count("configuration_revision_assets"));
        Assert.Equal(2, fixture.Count("configuration_batch_items"));
        Assert.Equal(2, fixture.Count("operations"));
        Assert.Null(fixture.Batches.Lookup(identity, next));
        fixture.Execute("DROP TRIGGER fixture_fail;");
        Assert.True(fixture.Batches.Commit(identity, prepared, DateTimeOffset.UtcNow).IsNew);
        Assert.All(next.Changes, change => Assert.Equal(2, fixture.Revisions.Read(change.Address)!.Revision));
    }

    [Fact(DisplayName = "A Stale Second Resource Or Binding Rejects The Whole Batch And Preserves Unrelated Binding Fields")]
    public void RejectsStaleResources()
    {
        var fixture = new ConfigurationBatchFixture();
        var main = fixture.Capture("z-main", "existing");
        fixture.Save(new([main]));
        ConfigurationRevisionBatch stale = new([fixture.Capture("a-extra", "not-saved"), fixture.Capture("z-main", "stale")]);
        Assert.Equal("configuration_revision_conflict", Assert.Throws<ConfigurationException>(() => fixture.Save(stale)).Message);
        Assert.Null(fixture.Revisions.Read(stale.Changes[0].Address));
        Assert.Equal(1, fixture.Revisions.Read(main.Address)!.Revision);
        var definitions = new AutomationDefinitionStore(fixture.Database);
        var catalog = definitions.Read();
        var binding = catalog.Bindings.Single(item => item.Id == fixture.Binding);
        var edits = new AutomationChanges(catalog.Revision, [], [], [], [binding with { IsEnabled = true }], [], [], []);
        definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", edits), edits, DateTimeOffset.UtcNow);
        var corrected = new ConfigurationRevisionBatch([stale.Changes[0], stale.Changes[1] with { ExpectedRevision = 1 }]);
        Assert.Equal("configuration_binding_revision_conflict", Assert.Throws<ConfigurationException>(() => fixture.Save(corrected)).Message);
        Assert.Null(fixture.Revisions.Read(stale.Changes[0].Address));
        Assert.True(definitions.Read().Bindings.Single(item => item.Id == fixture.Binding).IsEnabled);
        Assert.Equal(2, definitions.Read().Bindings.Single(item => item.Id == fixture.Binding).Revision);
    }

    [Fact(DisplayName = "Batch Replay After Recreation Returns Every Original Revision Without Rewinding Current Or Requiring Original Content")]
    public void ReplaysOriginalRevisions()
    {
        var fixture = new ConfigurationBatchFixture();
        ConfigurationRevisionBatch batch = new([fixture.Capture("main", "old-main"), fixture.Capture("extra", "old-extra")]);
        var identity = ConfigurationBatchStore.Identity(Guid.NewGuid(), "fixture", batch);
        fixture.Save(identity, batch);
        fixture.Save(new([fixture.Capture("main", "new-main", 1), fixture.Capture("extra", "new-extra", 1)]));
        foreach (var item in batch.Changes)
            File.Delete(Path.Combine(fixture.Instance.RuntimeRoot, $"blobs/content/{item.Tree.Entries[0].Sha256}.bin"));
        var reopened = new ConfigurationBatchStore(fixture.Instance, fixture.Database, new());
        var replay = reopened.Lookup(identity, batch)!;
        Assert.False(replay.IsNew);
        Assert.All(replay.Items, item => { Assert.False(item.IsNew); Assert.Equal(1, item.Saved.Revision); });
        Assert.All(batch.Changes, item => Assert.Equal(2, fixture.Revisions.Read(item.Address)!.Revision));
        var reversed = new ConfigurationRevisionBatch([batch.Changes[1], batch.Changes[0]]);
        Assert.NotNull(reopened.Lookup(ConfigurationBatchStore.Identity(identity.OperationId, "fixture", reversed), reversed));
        Assert.Throws<OperationConflictException>(() => reopened.Lookup(ConfigurationBatchStore.Identity(identity.OperationId, "other", batch), batch));
        Assert.Throws<OperationConflictException>(() => reopened.Lookup(identity, new([batch.Changes[0]])));
    }

    [Fact(DisplayName = "A No Change Repair In A Batch Keeps Its Version While An Independent Resource Advances")]
    public void PreservesUnchangedRevisions()
    {
        var fixture = new ConfigurationBatchFixture();
        var main = fixture.Capture("main", "unchanged");
        var extra = fixture.Capture("extra", "old-extra");
        fixture.Save(new([main, extra]));
        var result = fixture.Save(new([main with { ExpectedRevision = 1, Kind = ConfigurationUpdateKind.Repair }, fixture.Capture("extra", "new-extra", 1)]));
        var noChange = result.Items.Single(item => item.Saved.Address == main.Address);
        Assert.False(noChange.HasChanges); Assert.Empty(noChange.Delta.Changes);
        Assert.Equal(1, noChange.Saved.Revision);
        Assert.Equal(2, result.Items.Single(item => item.Saved.Address == extra.Address).Saved.Revision);
        Assert.Equal(3, fixture.Count("configuration_revisions"));
    }

    [Fact(DisplayName = "Duplicate Cross Account Or Differently Revised Bindings Are Rejected Before Preparation")]
    public void RejectsInvalidBindings()
    {
        var fixture = new ConfigurationBatchFixture();
        var main = fixture.Capture("main", "candidate");
        var extra = fixture.Capture("extra", "candidate");
        Assert.Throws<ConfigurationException>(() => fixture.Batches.Prepare(new([main, main])));
        Assert.Throws<ConfigurationException>(() => fixture.Batches.Prepare(new([main, extra with { Address = new(fixture.OtherBinding, "extra") }])));
        Assert.Throws<ConfigurationException>(() => fixture.Batches.Prepare(new([main, extra with { ExpectedBindingRevision = 2 }])));
        Assert.Throws<ConfigurationException>(() => fixture.Batches.Prepare(new([])));
        Assert.Equal(0, fixture.Count("configuration_revisions"));
    }

    [Fact(DisplayName = "Batch Preparation Holds All Payloads And Releases Them On Failure Or Disposal")]
    public void HoldsPreparedPayloads()
    {
        var fixture = new ConfigurationBatchFixture();
        var main = fixture.Capture("a-main", "main");
        var extra = fixture.Capture("z-extra", "extra");
        var path = Path.Combine(fixture.Instance.RuntimeRoot, $"blobs/content/{main.Tree.Entries[0].Sha256}.bin");
        var extraPath = Path.Combine(fixture.Instance.RuntimeRoot, $"blobs/content/{extra.Tree.Entries[0].Sha256}.bin");
        var extraBytes = File.ReadAllBytes(extraPath);
        File.Delete(extraPath);
        Assert.ThrowsAny<IOException>(() => fixture.Batches.Prepare(new([main, extra])));
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        File.WriteAllBytes(extraPath, extraBytes);
        ConfigurationRevisionBatch batch = new([main, extra]);
        var prepared = fixture.Batches.Prepare(batch);
        Assert.Throws<IOException>(() => File.Delete(path));
        Assert.Throws<IOException>(() => File.Delete(extraPath));
        prepared.Dispose();
        Assert.Throws<ObjectDisposedException>(() => fixture.Batches.Commit(ConfigurationBatchStore.Identity(Guid.NewGuid(), "fixture", batch), prepared, DateTimeOffset.UtcNow));
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }

    [Fact(DisplayName = "Competing Batches Have One Complete Winner And The Loser Does Not Consume An Operation Or Publish Any Item")]
    public async Task SerializesCompetingBatches()
    {
        var fixture = new ConfigurationBatchFixture();
        ConfigurationRevisionBatch first = new([fixture.Capture("main", "first-main"), fixture.Capture("extra", "first-extra")]);
        ConfigurationRevisionBatch second = new([fixture.Capture("extra", "second-extra"), fixture.Capture("main", "second-main")]);
        var batches = new[] { first, second };
        var operations = batches.Select(batch => ConfigurationBatchStore.Identity(Guid.NewGuid(), "fixture", batch)).ToArray();
        using var one = fixture.Batches.Prepare(first);
        using var two = fixture.Batches.Prepare(second);
        var prepared = new[] { one, two };
        var errors = await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Task.Run(() =>
            Record.Exception(() => fixture.Batches.Commit(operations[index], prepared[index], DateTimeOffset.UtcNow)))));
        Assert.Single(errors, error => error is null);
        var winner = Array.FindIndex(errors, error => error is null);
        var loser = 1 - winner;
        Assert.IsType<ConfigurationException>(errors[loser]);
        foreach (var change in batches[winner].Changes) Assert.Equal(change.Tree.Entries, fixture.Revisions.Read(change.Address)!.Tree.Entries);
        Assert.Null(fixture.Batches.Lookup(operations[loser], batches[loser]));
        Assert.Equal(2, fixture.Count("configuration_revisions"));
        Assert.Equal(2, fixture.Count("configuration_batch_items"));
        var retry = new ConfigurationRevisionBatch(batches[loser].Changes.Select(change => change with { ExpectedRevision = 1 }).ToImmutableArray());
        Assert.True(fixture.Save(ConfigurationBatchStore.Identity(operations[loser].OperationId, "fixture", retry), retry).IsNew);
    }

    [Fact(DisplayName = "Incomplete Persisted Batch Results Are Rejected Rather Than Returning Partial Success")]
    public void RejectsPartialResults()
    {
        var fixture = new ConfigurationBatchFixture();
        ConfigurationRevisionBatch batch = new([fixture.Capture("main", "main"), fixture.Capture("extra", "extra")]);
        var operation = ConfigurationBatchStore.Identity(Guid.NewGuid(), "fixture", batch);
        fixture.Save(operation, batch);
        fixture.Execute("DELETE FROM configuration_batch_items WHERE ordinal=1;");
        Assert.Equal("configuration_batch_result_unrecognized", Assert.Throws<IOException>(() => fixture.Batches.Lookup(operation, batch)).Message);
        Assert.All(batch.Changes, change => Assert.Equal(1, fixture.Revisions.Read(change.Address)!.Revision));
    }

    [Fact(DisplayName = "A Prepared Batch Cannot Publish To Another Instance")]
    public void RejectsForeignInstances()
    {
        var fixture = new ConfigurationBatchFixture();
        ConfigurationRevisionBatch batch = new([fixture.Capture("main", "candidate")]);
        using var prepared = fixture.Batches.Prepare(batch);
        var other = new ConfigurationBatchFixture();
        Assert.Equal("configuration_preparation_instance_unbound", Assert.Throws<IOException>(() => other.Batches.Commit(
            ConfigurationBatchStore.Identity(Guid.NewGuid(), "fixture", batch), prepared, DateTimeOffset.UtcNow)).Message);
        Assert.Equal(0, fixture.Count("configuration_revisions")); Assert.Equal(0, other.Count("configuration_revisions"));
    }

    [Fact(DisplayName = "An Accepted Batch Can Be Replayed During Host Closure Without Mutation Admission")]
    public void ReplaysDuringClosure()
    {
        var fixture = new ConfigurationBatchFixture();
        ConfigurationRevisionBatch batch = new([fixture.Capture("main", "main"), fixture.Capture("extra", "extra")]);
        var operation = ConfigurationBatchStore.Identity(Guid.NewGuid(), "fixture", batch);
        using var instanceLease = InstanceLease.Acquire(fixture.Instance, new());
        var index = new StartupJournalRecovery(fixture.Instance, new(), fixture.Database,
            new(fixture.Root, Path.Combine(fixture.Instance.RuntimeRoot, "extensions"))).Recover(instanceLease);
        var lifecycle = new HostLifecycle(index, TimeProvider.System);
        var service = new ConfigurationBatchService(fixture.Instance, fixture.Database, new(), instanceLease, new(lifecycle));
        Assert.True(service.Save(operation, batch, DateTimeOffset.UtcNow).IsNew);
        Assert.NotNull(lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid()).Permit);
        Assert.False(service.Save(operation, batch, DateTimeOffset.UtcNow).IsNew);
        Assert.Equal(2, fixture.Count("configuration_revisions"));
    }

    [Theory(DisplayName = "Batch Service Uses All Shared Resource Claims And Rejects Closure Or Recovery Before Any Publication")]
    [InlineData("extra")]
    [InlineData("main")]
    [InlineData("definitions")]
    [InlineData("blobs")]
    [InlineData("recovery")]
    [InlineData("closing")]
    public void RequiresSharedAdmission(string held)
    {
        var fixture = new ConfigurationBatchFixture();
        ConfigurationRevisionBatch batch = new([fixture.Capture("main", "main"), fixture.Capture("extra", "extra")]);
        using var instanceLease = InstanceLease.Acquire(fixture.Instance, new());
        var index = new StartupJournalRecovery(fixture.Instance, new(), fixture.Database,
            new(fixture.Root, Path.Combine(fixture.Instance.RuntimeRoot, "extensions"))).Recover(instanceLease);
        var lifecycle = new HostLifecycle(index, TimeProvider.System);
        var resources = new ResourceLeaseManager(lifecycle);
        var key = held == "definitions" ? "automation:definitions" : held == "blobs" ? "blobs:content" :
            held is "main" or "extra" ? ConfigurationRevisionService.ResourceKey(new(fixture.Binding, held)) : null;
        using var conflicting = key is null ? null : resources.TryAcquire(Guid.NewGuid(), [new(key, ResourceClaimMode.Mutation)]).Lease;
        if (held == "recovery") index.BlockUnknown("fixture_unknown");
        if (held == "closing") lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid());
        var service = new ConfigurationBatchService(fixture.Instance, fixture.Database, new(), instanceLease, resources);
        Assert.Throws<ConfigurationException>(() => service.Save(ConfigurationBatchStore.Identity(Guid.NewGuid(), "fixture", batch), batch, DateTimeOffset.UtcNow));
        Assert.Equal(0, fixture.Count("configuration_revisions"));
        Assert.Equal(0, fixture.Count("configuration_batch_items"));
    }
}
