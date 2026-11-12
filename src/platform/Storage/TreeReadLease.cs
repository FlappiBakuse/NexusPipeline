using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage;

internal sealed class TreeReadLease : IDisposable
{
    private readonly List<IDisposable> _held = [];
    private readonly object _gate = new();
    private readonly string _target;
    private readonly Dictionary<string, FileTreeEntry> _entries;
    private readonly Dictionary<string, PhysicalObjectIdentity> _identities;
    private readonly bool _application;
    private int _references = 1;
    private bool _released;

    private TreeReadLease(string target, FileTreeSnapshot snapshot, IReadOnlyList<PhysicalTreeObject> objects, bool application)
    {
        _target = target;
        _entries = snapshot.Entries.ToDictionary(item => item.RelativePath, StringComparer.Ordinal);
        _identities = objects.ToDictionary(item => item.RelativePath, item => item.Identity, StringComparer.Ordinal);
        _application = application;
    }

    internal static TreeReadLease Acquire(string target, PhysicalObjectIdentity parentIdentity, ArchivedFileTree before)
        => AcquireCore(target, parentIdentity, before, application: false);

    internal static TreeReadLease AcquireMetadata(string target, PhysicalObjectIdentity parentIdentity, ArchivedFileTree before)
    {
        FileTreeArchive.ValidateArchive(before);
        return AcquireSnapshot(target, parentIdentity, before.Snapshot, before.OriginalObjects, application: false, verifyContents: false);
    }

    internal static TreeReadLease AcquireApplication(string target, PhysicalObjectIdentity parentIdentity, ArchivedFileTree before)
        => AcquireCore(target, parentIdentity, before, application: true);

    internal static TreeReadLease AcquireApplication(string target, PhysicalObjectIdentity parentIdentity, FileTreeSnapshot snapshot,
        IReadOnlyList<PhysicalTreeObject> objects, bool exclusiveRoot)
    {
        ApplicationTree.Validate(snapshot, complete: true);
        return AcquireSnapshot(target, parentIdentity, snapshot, objects, application: !exclusiveRoot);
    }

    private static TreeReadLease AcquireCore(string target, PhysicalObjectIdentity parentIdentity, ArchivedFileTree before, bool application)
    {
        FileTreeArchive.ValidateArchive(before);
        if (application) ApplicationTree.Validate(before.Snapshot, complete: false);
        return AcquireSnapshot(target, parentIdentity, before.Snapshot, before.OriginalObjects, application);
    }

    private static TreeReadLease AcquireSnapshot(string target, PhysicalObjectIdentity parentIdentity, FileTreeSnapshot snapshot,
        IReadOnlyList<PhysicalTreeObject> objects, bool application, bool verifyContents = true)
    {
        using var timing = StartupProfile.Measure("payload.verify");
        var lease = new TreeReadLease(target, snapshot, objects, application);
        try
        {
            if (PhysicalPathPolicy.NormalizeWithoutLinks(target) != target) throw new IOException("file_tree_lease_path_not_canonical");
            var parent = Path.GetDirectoryName(target) ?? throw new IOException("file_tree_lease_parent_missing");
            lease._held.Add(StableDirectoryLease.Acquire(parent));
            if (PhysicalObjectIdentity.Read(parent) != parentIdentity) throw new IOException("file_tree_lease_parent_changed");
            foreach (var entry in snapshot.Entries.Where(item => item.Kind == FileObjectKind.Directory).OrderBy(item => item.RelativePath.Length))
                lease._held.Add(StableDirectoryLease.Acquire(Path.Combine(target, entry.RelativePath)));
            foreach (var entry in snapshot.Entries.Where(item => item.Kind == FileObjectKind.File))
                lease._held.Add(new FileStream(PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(target, entry.RelativePath)), FileMode.Open, FileAccess.Read, FileShare.Read));
            if (!verifyContents) { lease.RequireNamespace(); return lease; }
            var scanner = new FileTreeScanner(SnapshotBudget.Default);
            var actual = application ? ApplicationTree.Capture(scanner, target, default) : scanner.Capture(target, default);
            if (actual.Shape != snapshot.Shape || !actual.Entries.SequenceEqual(snapshot.Entries) ||
                !PhysicalTreeIdentity.Capture(target, actual).SequenceEqual(objects)) throw new IOException("file_tree_lease_original_changed");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal TreeBorrow Borrow()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_released, this);
            // Read-only file handles preserve verified bytes; borrowing still checks the current namespace.
            RequireNamespace();
            _references++;
            return new TreeBorrow(this);
        }
    }

    private void RequireNamespace()
    {
        var remaining = new HashSet<string>(_entries.Keys, StringComparer.Ordinal);
        var pending = new Stack<string>(); pending.Push(_target);
        while (pending.TryPop(out var path))
        {
            PhysicalPathPolicy.NormalizeWithoutLinks(path);
            var relative = path == _target ? "" : Path.GetRelativePath(_target, path).Replace('\\', '/');
            if (!remaining.Remove(relative)) throw new IOException("file_tree_lease_namespace_changed");
            var expected = _entries[relative];
            var attributes = File.GetAttributes(path);
            var kind = (attributes & FileAttributes.Directory) != 0 ? FileObjectKind.Directory : FileObjectKind.File;
            if (kind != expected.Kind || PhysicalObjectIdentity.Read(path) != _identities[relative] ||
                kind == FileObjectKind.File && new FileInfo(path).Length != expected.SizeBytes)
                throw new IOException("file_tree_lease_original_changed");
            if (kind == FileObjectKind.Directory)
                foreach (var child in Directory.EnumerateFileSystemEntries(path))
                {
                    if (_application && relative.Length == 0 && !ApplicationTree.Roots.Contains(Path.GetFileName(child), StringComparer.Ordinal)) continue;
                    if (pending.Count >= _entries.Count) throw new IOException("file_tree_lease_namespace_changed");
                    pending.Push(child);
                }
        }
        if (remaining.Count != 0) throw new IOException("file_tree_lease_namespace_changed");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_released) return;
            _released = true;
            ReleaseInside();
        }
    }

    private void ReleaseInside()
    {
        if (--_references != 0) return;
        for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
        _held.Clear();
    }

    internal sealed class TreeBorrow : IDisposable
    {
        private readonly TreeReadLease _owner;
        private bool _disposed;
        internal TreeBorrow(TreeReadLease owner) => _owner = owner;
        internal TreeBorrow Borrow()
        {
            lock (_owner._gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _owner.RequireNamespace();
                _owner._references++;
                return new(_owner);
            }
        }

        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (_disposed) return;
                _disposed = true;
                _owner.ReleaseInside();
            }
        }
    }
}
