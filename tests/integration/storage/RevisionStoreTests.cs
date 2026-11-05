using System.Security.Cryptography;
using System.Text;
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

public sealed class RevisionStoreTests
{
    [Theory(DisplayName = "Configuration Saves Require Their Live Instance And Shared Definition Blob And Slot Admission")]
    [InlineData("slot")]
    [InlineData("definitions")]
    [InlineData("blobs")]
    [InlineData("recovery")]
    [InlineData("closing")]
    [InlineData("expired")]
    public void RequiresSharedAdmission(string fault)
    {
        var fixture = Create();
        var change = Capture(fixture, "main", "candidate");
        using var instanceLease = InstanceLease.Acquire(fixture.Instance, new());
        var index = new StartupJournalRecovery(fixture.Instance, new(), fixture.Database,
            new(fixture.Root, Path.Combine(fixture.Instance.RuntimeRoot, "extensions"))).Recover(instanceLease);
        var lifecycle = new HostLifecycle(index, TimeProvider.System);
        var resources = new ResourceLeaseManager(lifecycle);
        var service = new ConfigurationRevisionService(fixture.Instance, fixture.Database, new(), instanceLease, resources);
        var heldKey = fault == "slot" ? ConfigurationRevisionService.ResourceKey(change.Address)
            : fault == "definitions" ? "automation:definitions" : fault == "blobs" ? "blobs:content" : null;
        using var conflict = heldKey is null ? null : resources.TryAcquire(Guid.NewGuid(), [new(heldKey, ResourceClaimMode.Mutation)]).Lease;
        if (fault == "recovery") index.BlockUnknown("fixture_unknown");
        if (fault == "closing") lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid());
        if (fault == "expired") instanceLease.Dispose();
        Assert.NotNull(Record.Exception(() => service.Save(Identity(change), change, DateTimeOffset.UtcNow)));
        Assert.Null(fixture.Store.Read(change.Address));
        Assert.Equal(0L, Scalar(fixture, "SELECT count(*) FROM configuration_revisions;"));
    }

    [Fact(DisplayName = "A Configuration Save Checks The Binding Revision And Can Retry Its Unaccepted Identity Against The Current Definition")]
    public void ChecksBindingRevisions()
    {
        var fixture = Create();
        var change = Capture(fixture, "main", "candidate");
        var definitions = new AutomationDefinitionStore(fixture.Database);
        var catalog = definitions.Read();
        var binding = catalog.Bindings.Single(value => value.Id == fixture.Binding);
        var edits = new AutomationChanges(catalog.Revision, [], [], [], [binding with { IsEnabled = true }], [], [], []);
        definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", edits), edits, DateTimeOffset.UtcNow);
        var operation = Identity(change);
        Assert.Equal("configuration_binding_revision_conflict", Assert.Throws<ConfigurationException>(() =>
            fixture.Store.Commit(operation, change, DateTimeOffset.UtcNow)).Message);
        Assert.Null(fixture.Store.Read(change.Address));
        var corrected = change with { ExpectedBindingRevision = 2 };
        Assert.True(fixture.Store.Commit(ConfigurationRevisionStore.Identity(operation.OperationId, "fixture", corrected), corrected, DateTimeOffset.UtcNow).IsNew);
        Assert.True(definitions.Read().Bindings.Single(value => value.Id == fixture.Binding).IsEnabled);
        Assert.Equal(2, definitions.Read().Bindings.Single(value => value.Id == fixture.Binding).Revision);
    }

    [Fact(DisplayName = "An Accepted Configuration Result Can Be Read During Host Closure Without Reentering Mutation Admission")]
    public void ReplaysDuringClosure()
    {
        var fixture = Create();
        var change = Capture(fixture, "main", "candidate");
        var operation = Identity(change);
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        var index = new StartupJournalRecovery(fixture.Instance, new(), fixture.Database,
            new(fixture.Root, Path.Combine(fixture.Instance.RuntimeRoot, "extensions"))).Recover(lease);
        var lifecycle = new HostLifecycle(index, TimeProvider.System);
        var service = new ConfigurationRevisionService(fixture.Instance, fixture.Database, new(), lease, new(lifecycle));
        Assert.True(service.Save(operation, change, DateTimeOffset.UtcNow).IsNew);
        Assert.NotNull(lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid()).Permit);
        Assert.False(service.Save(operation, change, DateTimeOffset.UtcNow).IsNew);
        Assert.Equal(1, service.Read(change.Address)!.Revision);
    }

    [Fact(DisplayName = "Main And Additional Configuration Revisions Reopen Independently Without Adopting Another Accounts Snapshot")]
    public void SeparatesAccountSnapshots()
    {
        var fixture = Create();
        var main = Capture(fixture, "main", "main-one");
        var extra = Capture(fixture, "extra:settings/profile", "extra-one");
        Save(fixture, main); Save(fixture, extra);
        var reopened = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "diagnostics"), new JournalStartupAdmission(fixture.Instance, new()));
        var store = new ConfigurationRevisionStore(fixture.Instance, reopened, new());
        Assert.Equal(main.Tree.Entries, store.Read(main.Address)!.Tree.Entries);
        Assert.Equal(extra.Tree.Entries, store.Read(extra.Address)!.Tree.Entries);
        Assert.Null(store.Read(new(fixture.OtherBinding, "main")));
        Assert.Equal(fixture.Binding, store.Read(main.Address)!.Address.BindingId);
        Assert.Equal(1, store.Read(extra.Address)!.Revision);
    }

    [Fact(DisplayName = "A Real Two Hundred File Update Reuses Every Existing Blob And Publishes Only The Measured Delta")]
    public void ReusesUnchangedBlobs()
    {
        var fixture = Create();
        var directory = Path.Combine(fixture.Root, "configuration");
        Directory.CreateDirectory(directory);
        for (var index = 0; index < 200; index++) File.WriteAllText(Path.Combine(directory, $"{index:D3}.json"), $"original-{index:D3}");
        var first = CaptureTree(fixture, "main", directory);
        Save(fixture, first);
        var content = Path.Combine(fixture.Instance.RuntimeRoot, "blobs/content");
        var identities = Directory.EnumerateFiles(content).ToDictionary(path => path, PhysicalObjectIdentity.Read);
        File.Delete(Path.Combine(directory, "199.json"));
        for (var index = 1; index <= 3; index++) File.WriteAllText(Path.Combine(directory, $"{index:D3}.json"), $"modified-{index:D3}");
        File.WriteAllText(Path.Combine(directory, "200.json"), "added-200");
        File.WriteAllText(Path.Combine(directory, "201.json"), "added-201");
        var next = CaptureTree(fixture, "main", directory) with { ExpectedRevision = 1, Kind = ConfigurationUpdateKind.Progress };
        var result = Save(fixture, next);
        Assert.Equal((2, 3, 1), (result.Delta.AddedFiles, result.Delta.ModifiedFiles, result.Delta.RemovedFiles));
        Assert.Equal(6, result.Delta.Changes.Length);
        Assert.Equal(205, Directory.EnumerateFiles(content).Count());
        foreach (var pair in identities) Assert.Equal(pair.Value, PhysicalObjectIdentity.Read(pair.Key));
        Assert.Equal(2, fixture.Store.Read(next.Address)!.Revision);
        var old = first.Tree.Entries.First(entry => entry.RelativePath == "199.json");
        Assert.True(new BlobReferenceIndex(fixture.Instance, new(), fixture.Database, new(fixture.Instance, new()))
            .IsReferenced(new($"blobs/content/{old.Sha256}.bin", old.SizeBytes, old.Sha256!)));
    }

    [Fact(DisplayName = "Replaying An Older Configuration Save Returns Its Original Revision Without Rewinding The Current Snapshot")]
    public void KeepsOriginalRevisions()
    {
        var fixture = Create();
        var first = Capture(fixture, "main", "first");
        var identity = Identity(first);
        using (var prepared = fixture.Store.Prepare(first)) fixture.Store.Commit(identity, prepared, DateTimeOffset.UtcNow);
        Save(fixture, Capture(fixture, "main", "second") with { ExpectedRevision = 1 });
        var result = fixture.Store.Lookup(identity, first)!;
        Assert.False(result.IsNew);
        Assert.Equal(1, result.Saved.Revision);
        Assert.Equal(first.Tree.Entries, result.Saved.Tree.Entries);
        Assert.Equal(2, fixture.Store.Read(first.Address)!.Revision);
        Assert.Throws<OperationConflictException>(() => fixture.Store.Lookup(
            ConfigurationRevisionStore.Identity(identity.OperationId, "different-actor", first), first));
    }

    [Fact(DisplayName = "Concurrent Configuration Cas Has One Winner And Does Not Consume The Losing Operation Identity")]
    public async Task SerializesSnapshotCas()
    {
        var fixture = Create();
        var changes = new[] { Capture(fixture, "main", "first"), Capture(fixture, "main", "second") };
        var operations = changes.Select(Identity).ToArray();
        using var first = fixture.Store.Prepare(changes[0]);
        using var second = fixture.Store.Prepare(changes[1]);
        var preparations = new[] { first, second };
        var errors = await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Task.Run(() =>
            Record.Exception(() => fixture.Store.Commit(operations[index], preparations[index], DateTimeOffset.UtcNow)))));
        Assert.Single(errors, error => error is null);
        var loser = Array.FindIndex(errors, error => error is not null);
        Assert.IsType<ConfigurationException>(errors[loser]);
        var corrected = changes[loser] with { ExpectedRevision = 1 };
        using var retry = fixture.Store.Prepare(corrected);
        Assert.True(fixture.Store.Commit(ConfigurationRevisionStore.Identity(operations[loser].OperationId, "fixture", corrected), retry, DateTimeOffset.UtcNow).IsNew);
        Assert.Equal(2, fixture.Store.Read(corrected.Address)!.Revision);
    }

    [Fact(DisplayName = "Failed Configuration Publication Rolls Back The Pointer Assets Revision And Accepted Operation Together")]
    public void RevertsPublicationFailure()
    {
        var fixture = Create();
        var first = Capture(fixture, "main", "first");
        Save(fixture, first);
        var change = Capture(fixture, "main", "second") with { ExpectedRevision = 1 };
        using var prepared = fixture.Store.Prepare(change);
        var identity = Identity(change);
        Execute(fixture, "CREATE TRIGGER fixture_fail_configuration BEFORE INSERT ON configuration_mutations BEGIN SELECT RAISE(ABORT,'fixture_failure'); END;");
        Assert.Throws<SqliteException>(() => fixture.Store.Commit(identity, prepared, DateTimeOffset.UtcNow));
        Assert.Equal(first.Tree.Entries, fixture.Store.Read(first.Address)!.Tree.Entries);
        Assert.Equal(1L, Scalar(fixture, "SELECT count(*) FROM configuration_revisions;"));
        Assert.Null(fixture.Store.Lookup(identity, change));
        Execute(fixture, "DROP TRIGGER fixture_fail_configuration;");
        Assert.True(fixture.Store.Commit(identity, prepared, DateTimeOffset.UtcNow).IsNew);
        Assert.Equal(2, fixture.Store.Read(change.Address)!.Revision);
    }

    [Theory(DisplayName = "Missing Or Damaged Prepared Configuration Content Cannot Publish Any Revision")]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsMissingContent(bool missing)
    {
        var fixture = Create();
        var change = Capture(fixture, "main", "candidate");
        var entry = change.Tree.Entries.Single();
        var path = Path.Combine(fixture.Instance.RuntimeRoot, $"blobs/content/{entry.Sha256}.bin");
        if (missing) File.Delete(path); else File.WriteAllText(path, "foreign");
        Assert.ThrowsAny<IOException>(() => fixture.Store.Prepare(change));
        Assert.Null(fixture.Store.Read(change.Address));
        Assert.Equal(0L, Scalar(fixture, "SELECT count(*) FROM configuration_revisions;"));
    }

    [Fact(DisplayName = "Prepared Configuration Holds Its Manifest And Payload And Cannot Be Committed After Disposal")]
    public void HoldsPreparedInputs()
    {
        var fixture = Create();
        var change = Capture(fixture, "main", "candidate");
        var path = Path.Combine(fixture.Instance.RuntimeRoot, $"blobs/content/{change.Tree.Entries.Single().Sha256}.bin");
        var prepared = fixture.Store.Prepare(change);
        Assert.Throws<IOException>(() => File.WriteAllText(path, "changed"));
        Assert.Throws<IOException>(() => File.Delete(path));
        prepared.Dispose();
        Assert.Throws<ObjectDisposedException>(() => fixture.Store.Commit(Identity(change), prepared, DateTimeOffset.UtcNow));
        Assert.Null(fixture.Store.Read(change.Address));
    }

    [Theory(DisplayName = "Unknown Configuration Manifest Or Missing Current Pointer Rejects Startup Without Repairing The Original")]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservesUnknownManifests(bool missingPointer)
    {
        var fixture = Create();
        Save(fixture, Capture(fixture, "main", "saved"));
        if (missingPointer) Execute(fixture, "DELETE FROM configuration_current;");
        else
        {
            var bytes = Encoding.UTF8.GetBytes("{\"format\":\"nexus.configuration.snapshot\",\"version\":2,\"tree\":{}}");
            var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var relative = $"blobs/plans/{digest}.json";
            File.WriteAllBytes(Path.Combine(fixture.Instance.RuntimeRoot, relative), bytes);
            using var connection = Connection(fixture);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE configuration_revisions SET manifest_path=$path,manifest_digest=$digest,manifest_size=$size;";
            command.Parameters.AddWithValue("$path", relative);
            command.Parameters.AddWithValue("$digest", digest);
            command.Parameters.AddWithValue("$size", bytes.Length);
            command.ExecuteNonQuery();
        }
        var before = DatabaseBytes(fixture.Database.DatabasePath);
        Assert.Throws<IOException>(() => StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "diagnostics"), new JournalStartupAdmission(fixture.Instance, new())));
        Assert.Equal(before, DatabaseBytes(fixture.Database.DatabasePath));
    }

    private static ConfigurationSaveResult Save(Fixture fixture, ConfigurationRevisionChange change)
    {
        using var prepared = fixture.Store.Prepare(change);
        return fixture.Store.Commit(Identity(change), prepared, DateTimeOffset.UtcNow);
    }
    private static OperationIdentity Identity(ConfigurationRevisionChange change) => ConfigurationRevisionStore.Identity(Guid.NewGuid(), "fixture", change);
    private static ConfigurationRevisionChange Capture(Fixture fixture, string resource, string value)
    {
        var path = Path.Combine(fixture.Root, Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, value);
        return CaptureTree(fixture, resource, path);
    }
    private static ConfigurationRevisionChange CaptureTree(Fixture fixture, string resource, string path)
    {
        var scanner = new FileTreeScanner(SnapshotBudget.Default);
        var tree = new FileTreeArchive(scanner, new(fixture.Instance, new())).Capture(path, default);
        return new(new(fixture.Binding, resource), 0, 1, new(1, new('a', 64), null, false), tree.Snapshot, ConfigurationUpdateKind.Manual);
    }
    private static Fixture Create()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "config-revisions-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        var changes = AutomationDefinitionSeed.Create(root);
        var definitions = new AutomationDefinitionStore(database);
        definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes), changes, DateTimeOffset.UtcNow);
        return new(root, instance, database, new(instance, database, new()), changes.Bindings[0].Id, changes.Bindings[1].Id);
    }
    private static SqliteConnection Connection(Fixture fixture)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Database.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
    private static void Execute(Fixture fixture, string sql)
    {
        using var connection = Connection(fixture);
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    private static object Scalar(Fixture fixture, string sql)
    {
        using var connection = Connection(fixture);
        using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar()!;
    }
    private static byte[] DatabaseBytes(string path) => new[] { "", "-wal", "-shm" }
        .Where(suffix => File.Exists(path + suffix)).SelectMany(suffix => File.ReadAllBytes(path + suffix)).ToArray();
    private sealed record Fixture(string Root, RuntimeInstance Instance, StateDatabase Database, ConfigurationRevisionStore Store, Guid Binding, Guid OtherBinding);
}
