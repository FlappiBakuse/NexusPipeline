using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;

namespace NexusPipeline.StorageTests;

internal sealed class ConfigurationEditFixture
{
    internal ConfigurationBatchFixture Base { get; } = new();
    internal ConfigurationEditStore Store { get; }
    internal ConfigurationEditExchange Exchange { get; }
    internal ConfigurationEditRequest Request { get; }
    internal ConfigurationRevisionBatch Initial { get; }
    internal Dictionary<string, (FileTreeSnapshot Tree, IReadOnlyList<PhysicalTreeObject> Objects)> Originals { get; } = [];
    internal Guid SessionId { get; } = Guid.NewGuid();
    internal ConfigurationEditStatus Session { get; private set; }

    internal ConfigurationEditFixture(string shape = "file", string[]? resourceIds = null,
        EditWritePolicy? writePolicy = null, bool directorySnapshot = false)
    {
        var resources = resourceIds ?? ["main", "extra"];
        ConfigurationRevisionChange Snapshot(string id)
        {
            if (!directorySnapshot) return Base.Capture(id, "snapshot-" + id);
            var root = Path.Combine(Base.Root, "snapshot-" + id); Directory.CreateDirectory(Path.Combine(root, "selected"));
            File.WriteAllText(Path.Combine(root, "selected/task.json"), "selected-old"); File.WriteAllText(Path.Combine(root, "guard.json"), "guard-original");
            return Base.CaptureTree(id, root);
        }
        Initial = new(resources.Select(Snapshot).ToImmutableArray());
        Base.Save(Initial);
        var targets = ImmutableArray.CreateBuilder<ConfigurationTarget>();
        foreach (var change in Initial.Changes)
        {
            var path = Path.Combine(Base.Root, Guid.NewGuid().ToString("N"));
            if (shape == "file") File.WriteAllText(path, "original-" + change.Address.ResourceId);
            if (shape == "directory") { Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "original.txt"), "original-" + change.Address.ResourceId); }
            var scanner = new FileTreeScanner(SnapshotBudget.Default);
            var tree = scanner.Capture(path, default);
            Originals.Add(path, (tree, PhysicalTreeIdentity.Capture(path, tree)));
            targets.Add(new ConfigurationTarget(change.Address, 1, change.Contract, path) { WritePolicy = writePolicy });
        }
        Request = new(1, targets.ToImmutable());
        Store = new(Base.Instance, Base.Database, new()); Exchange = new(Base.Instance, Base.Database, new());
        using var prepared = Store.Prepare(SessionId, Request);
        Session = Store.Begin(ConfigurationEditStore.BeginIdentity(SessionId, "fixture", Request), prepared, DateTimeOffset.UtcNow);
    }

    internal void MaterializeAll() { foreach (var resource in Session.Resources) Exchange.Materialize(resource.JournalId); }
    internal void InstallAll()
    {
        foreach (var resource in Session.Resources) Exchange.Install(resource.JournalId);
        Session = Store.MarkEditing(SessionId, "fixture", 1);
    }
    internal void RestoreAll()
    {
        foreach (var resource in Session.Resources)
        {
            var result = Exchange.Restore(resource.JournalId);
            if (result is not (ConfigurationRecoveryResult.NotExecuted or ConfigurationRecoveryResult.Restored or ConfigurationRecoveryResult.AlreadyCompleted))
                throw new IOException("Fixture restoration quarantined");
        }
    }
    internal ConfigurationEditCompletion Completion() => new(SessionId, Session.Revision,
        new(Initial.Changes.Select(change => change with { ExpectedRevision = 1 }).ToImmutableArray()));
    internal void VerifyOriginals()
    {
        foreach (var pair in Originals)
        {
            var tree = new FileTreeScanner(SnapshotBudget.Default).Capture(pair.Key, default);
            Xunit.Assert.Equal(pair.Value.Tree.Shape, tree.Shape); Xunit.Assert.Equal(pair.Value.Tree.Entries, tree.Entries);
            Xunit.Assert.Equal(pair.Value.Objects, PhysicalTreeIdentity.Capture(pair.Key, tree));
        }
    }
}
