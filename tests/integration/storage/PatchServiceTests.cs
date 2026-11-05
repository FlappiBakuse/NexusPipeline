using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class PatchServiceTests
{
    [Theory(DisplayName = "Declared Task Choices And Initial Restoration Round Trip Through The New Wire And Actual Snapshot Cas")]
    [InlineData("objects")]
    [InlineData("map")]
    [InlineData("parallel")]
    public void RoundTripsSelections(string shape)
    {
        var body = shape switch
        {
            "objects" => "\"tasks\":[{\"id\":\"A\",\"enabled\":true},{\"id\":\"B\",\"enabled\":false},{\"id\":\"other\",\"enabled\":true}]",
            "map" => "\"tasks\":{\"A\":true,\"B\":false,\"other\":true}",
            _ => "\"ids\":[\"A\",\"B\",\"other\"],\"states\":[true,false,true]"
        };
        using var fixture = new Fixture("{" + body + ",\"secret\":\"fixture-private\"}\r\n");
        var tasks = new ConfigurationSelector([new ConfigurationProperty("tasks")]);
        var ids = new ConfigurationSelector([new ConfigurationProperty("ids")]);
        var states = new ConfigurationSelector([new ConfigurationProperty("states")]);
        ConfigurationTaskSource declaration = shape switch
        {
            "objects" => new ConfigurationObjectTasks(tasks, "id", "enabled"),
            "map" => new ConfigurationMapTasks(tasks),
            _ => new ConfigurationParallelTasks(ids, states)
        };
        var access = new ConfigurationDocumentAccess([new(tasks, true), new(ids, true), new(states, true)],
            [new(tasks, true), new(states, true)], [new([new ConfigurationProperty("secret")])]);
        ConfigurationDocument Open()
        {
            var revision = fixture.Store.Read(fixture.Address)!;
            return new(new(Guid.NewGuid(), "main", revision.Revision, revision.Tree.Entries[0].Sha256!), ConfigurationSyntax.Json,
                access, Encoding.UTF8.GetBytes(fixture.CurrentText()));
        }
        using var original = Open();
        var initial = TaskSelectionPolicy.Capture(original, new(declaration, ["A", "B"]));
        var writer = new ConfigurationPatchWriter(new());
        var request = writer.Write(TaskSelectionPolicy.Select(original, initial, [new("A", false), new("B", true)]));
        var selected = fixture.Apply(request);
        Assert.Equal(2, selected.Saved.Revision);
        Assert.NotEqual(fixture.Original, fixture.CurrentText());
        Assert.Equal(fixture.Original, File.ReadAllText(fixture.Source));
        using var current = Open();
        var restored = fixture.Apply(writer.Write(TaskSelectionPolicy.Restore(current, initial)));
        Assert.Equal(3, restored.Saved.Revision);
        Assert.Equal(fixture.Original, fixture.CurrentText());
        Assert.Equal(fixture.Original, File.ReadAllText(fixture.Source));
        Assert.Null(fixture.Store.Read(new(fixture.OtherBinding, "main")));
    }

    [Fact(DisplayName = "A Recreated Patch Use Case Replays Its Durable Result Before Reading The Retired Source Content")]
    public void ReplaysDurablePatch()
    {
        using var fixture = new Fixture();
        var request = fixture.Request(Guid.NewGuid(), ["flag"], true, false);
        var before = fixture.Store.Read(fixture.Address)!.Tree.Entries.Single();
        Assert.Equal(2, fixture.Apply(request).Saved.Revision);
        File.Delete(Path.Combine(fixture.Content, before.Sha256 + ".bin"));
        var reloaded = new ConfigurationPatchService(fixture.Instance, fixture.Database, new(), fixture.Lease, fixture.Resources);
        var result = fixture.Apply(request, reloaded);
        Assert.False(result.IsNew);
        Assert.Equal(2, result.Saved.Revision);
        Assert.False(File.Exists(Path.Combine(fixture.Content, before.Sha256 + ".bin")));
        Assert.Equal(2, fixture.Store.Read(fixture.Address)!.Revision);
    }

    [Fact(DisplayName = "A Configuration Patch Preview Has No Persistent Effects And Commit Publishes Only The Selected Owned Snapshot")]
    public void PublishesOwnedSnapshots()
    {
        using var fixture = new Fixture();
        var original = fixture.Store.Read(fixture.Address)!;
        var before = Directory.EnumerateFiles(fixture.Content).ToDictionary(path => path, PhysicalObjectIdentity.Read);
        var bytes = fixture.Request(Guid.NewGuid(), ["flag"], true, false);
        using var preview = fixture.Preview(bytes);
        Assert.True(preview.HasChanges);
        Assert.Equal(1, fixture.Store.Read(fixture.Address)!.Revision);
        Assert.Equal(before.Keys, Directory.EnumerateFiles(fixture.Content));
        Assert.DoesNotContain("fixture-private", JsonSerializer.Serialize(preview));
        var result = fixture.Service.Commit(preview, DateTimeOffset.UtcNow);
        Assert.Equal(2, result.Saved.Revision);
        Assert.True(result.HasChanges);
        Assert.Equal("{\"flag\":false, \"secret\":\"fixture-private\", \"untouched\":7}\r\n", fixture.CurrentText());
        Assert.Equal(fixture.Original, File.ReadAllText(fixture.Source));
        foreach (var entry in before) Assert.Equal(entry.Value, PhysicalObjectIdentity.Read(entry.Key));
        Assert.Null(fixture.Store.Read(new(fixture.OtherBinding, "main")));
        Assert.Equal(original.Contract, result.Saved.Contract);
    }

    [Fact(DisplayName = "Two Different Typed Selectors Cannot Reuse An Operation Even When Their Verified Patch Bytes Are Identical")]
    public void BindsTypedSelectors()
    {
        using var fixture = new Fixture("{\"items\":[{\"id\":\"one\",\"enabled\":true}]}");
        var operation = Guid.NewGuid();
        using var indexed = fixture.Preview(fixture.Request(operation, ["items", 0, "enabled"], true, false));
        using var stable = fixture.Preview(fixture.Request(operation, ["items", new { match = new { property = "id", value = "one" } }, "enabled"], true, false));
        fixture.Service.Commit(indexed, DateTimeOffset.UtcNow);
        Assert.Throws<OperationConflictException>(() => fixture.Service.Commit(stable, DateTimeOffset.UtcNow));
        Assert.Equal(2, fixture.Store.Read(fixture.Address)!.Revision);
    }

    [Fact(DisplayName = "A Completed Patch Replay Returns Its Original Result After Another Configuration Revision Was Saved")]
    public void ReplaysOriginalResults()
    {
        using var fixture = new Fixture();
        using var preview = fixture.Preview(fixture.Request(Guid.NewGuid(), ["flag"], true, false));
        fixture.Service.Commit(preview, DateTimeOffset.UtcNow);
        fixture.SaveManual("{\"flag\":true,\"newer\":true}", 2);
        var replay = fixture.Service.Commit(preview, DateTimeOffset.UtcNow);
        Assert.False(replay.IsNew);
        Assert.Equal(2, replay.Saved.Revision);
        Assert.Equal(3, fixture.Store.Read(fixture.Address)!.Revision);
        Assert.Contains("newer", fixture.CurrentText());
    }

    [Fact(DisplayName = "A No Change Repair Preserves The Original Spelling And Does Not Create A New Configuration Revision")]
    public void PreservesUnchangedRepairs()
    {
        const string source = "{\"action\":\"\\u004eone\", \"secret\":\"fixture-private\"}\r\n";
        using var fixture = new Fixture(source);
        using var preview = fixture.Preview(fixture.Request(Guid.NewGuid(), ["action"], "None", "None"));
        Assert.False(preview.HasChanges);
        var result = fixture.Service.Commit(preview, DateTimeOffset.UtcNow);
        Assert.False(result.HasChanges);
        Assert.True(result.IsNew);
        Assert.Empty(result.Delta.Changes);
        Assert.Equal(1, result.Saved.Revision);
        Assert.Equal(source, fixture.CurrentText());
        Assert.Single(Directory.EnumerateFiles(fixture.Content));
        Assert.False(fixture.Service.Commit(preview, DateTimeOffset.UtcNow).IsNew);
    }

    [Theory(DisplayName = "Patch Commit Cannot Write Candidate Content When Its Shared Admission Is Unavailable")]
    [InlineData("conflict")]
    [InlineData("closing")]
    [InlineData("recovery")]
    public void RequiresPatchAdmission(string reason)
    {
        using var fixture = new Fixture();
        using var preview = fixture.Preview(fixture.Request(Guid.NewGuid(), ["flag"], true, false));
        using var conflict = reason == "conflict" ? fixture.Resources.TryAcquire(Guid.NewGuid(),
            [new(ConfigurationRevisionService.ResourceKey(fixture.Address), ResourceClaimMode.Mutation)]).Lease : null;
        if (reason == "closing") fixture.Lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid());
        if (reason == "recovery") fixture.Index.BlockUnknown("fixture_unknown");
        Assert.Throws<ConfigurationException>(() => fixture.Service.Commit(preview, DateTimeOffset.UtcNow));
        Assert.Single(Directory.EnumerateFiles(fixture.Content));
        Assert.Equal(1, fixture.Store.Read(fixture.Address)!.Revision);
    }

    [Fact(DisplayName = "A Stale Patch Cannot Overwrite A Snapshot That Changed After Its Read Only Preview")]
    public void RejectsStalePatch()
    {
        using var fixture = new Fixture();
        using var preview = fixture.Preview(fixture.Request(Guid.NewGuid(), ["flag"], true, false));
        fixture.SaveManual("{\"other\":\"newer\"}", 1);
        Assert.Equal("configuration_revision_conflict", Assert.Throws<ConfigurationException>(() =>
            fixture.Service.Commit(preview, DateTimeOffset.UtcNow)).Message);
        Assert.Equal("{\"other\":\"newer\"}", fixture.CurrentText());
        Assert.Equal(2, fixture.Store.Read(fixture.Address)!.Revision);
    }

    [Fact(DisplayName = "A Protected Configuration Field Cannot Become A Stored Repair Through The Snapshot Service")]
    public void ProtectsStoredFields()
    {
        using var fixture = new Fixture();
        Assert.Equal("configuration_access_denied", Assert.Throws<ConfigurationException>(() =>
            fixture.Preview(fixture.Request(Guid.NewGuid(), ["secret"], "fixture-private", "changed"))).Message);
        Assert.Single(Directory.EnumerateFiles(fixture.Content));
        Assert.Equal(1, fixture.Store.Read(fixture.Address)!.Revision);
    }

    private sealed class Fixture : IDisposable
    {
        internal RuntimeInstance Instance { get; }
        internal InstanceLease Lease { get; }
        internal StateDatabase Database { get; }
        internal ConfigurationRevisionStore Store { get; }
        internal ConfigurationPatchService Service { get; }
        internal ResourceLeaseManager Resources { get; }
        internal RecoveryIndex Index { get; }
        internal HostLifecycle Lifecycle { get; }
        internal ConfigurationAddress Address { get; }
        internal Guid OtherBinding { get; }
        internal string Original { get; }
        internal string Source { get; }
        internal string Content => Path.Combine(Instance.RuntimeRoot, "blobs/content");
        private readonly string _root;
        private readonly ConfigurationContractStamp _contract = new(1, new('a', 64), null, false);

        internal Fixture(string original = "{\"flag\":true, \"secret\":\"fixture-private\", \"untouched\":7}\r\n")
        {
            _root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "patch-service-" + Guid.NewGuid().ToString("N"));
            Instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(_root, "runtime"));
            var database = StateDatabase.CreateFresh(Instance);
            Database = database;
            var changes = AutomationDefinitionSeed.Create(_root);
            new AutomationDefinitionStore(database).Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes), changes, DateTimeOffset.UtcNow);
            Address = new(changes.Bindings[0].Id, "main"); OtherBinding = changes.Bindings[1].Id;
            Store = new(Instance, database, new());
            Original = original; Source = Path.Combine(_root, "source.json"); File.WriteAllText(Source, original);
            SaveManual(original, 0);
            Lease = InstanceLease.Acquire(Instance, new());
            Index = new StartupJournalRecovery(Instance, new(), database, new(_root, Path.Combine(Instance.RuntimeRoot, "extensions"))).Recover(Lease);
            Lifecycle = new(Index, TimeProvider.System); Resources = new(Lifecycle);
            Service = new(Instance, database, new(), Lease, Resources);
        }

        internal void SaveManual(string text, long expected)
        {
            var path = Path.Combine(_root, Guid.NewGuid().ToString("N")); File.WriteAllText(path, text);
            var scanner = new FileTreeScanner(SnapshotBudget.Default);
            var tree = new FileTreeArchive(scanner, new(Instance, new())).Capture(path, default);
            var change = new ConfigurationRevisionChange(Address, expected, 1, _contract, tree.Snapshot, ConfigurationUpdateKind.Manual);
            Store.Commit(ConfigurationRevisionStore.Identity(Guid.NewGuid(), "fixture", change), change, DateTimeOffset.UtcNow);
        }

        internal byte[] Request(Guid operation, object[] selector, object? expected, object? value)
        {
            var current = Store.Read(Address)!;
            return JsonSerializer.SerializeToUtf8Bytes(new { format = "nexus.config.patch", version = 1, operationId = operation,
                resourceId = Address.ResourceId, sourceSha256 = current.Tree.Entries.Single().Sha256, expectedRevision = current.Revision,
                edits = new[] { new { selector, expected, value } } });
        }

        internal PreparedConfigurationPatch Preview(byte[] bytes)
        {
            var empty = new ConfigurationSelector(ImmutableArray<ConfigurationSelectorStep>.Empty);
            var secret = new ConfigurationSelector([new ConfigurationProperty("secret")]);
            return Service.Preview("fixture", Address, 1, _contract, "", ConfigurationSyntax.Json,
                new([], [new(empty, true)], [secret]), bytes);
        }

        internal ConfigurationSaveResult Apply(byte[] bytes, ConfigurationPatchService? service = null)
        {
            var empty = new ConfigurationSelector(ImmutableArray<ConfigurationSelectorStep>.Empty);
            var secret = new ConfigurationSelector([new ConfigurationProperty("secret")]);
            return (service ?? Service).Apply("fixture", Address, 1, _contract, "", ConfigurationSyntax.Json,
                new([], [new(empty, true)], [secret]), bytes, DateTimeOffset.UtcNow);
        }

        internal string CurrentText()
        {
            var entry = Store.Read(Address)!.Tree.Entries.Single();
            return File.ReadAllText(Path.Combine(Content, entry.Sha256 + ".bin"));
        }
        public void Dispose() => Lease.Dispose();
    }
}
