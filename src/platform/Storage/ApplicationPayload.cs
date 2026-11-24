using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using System.Collections.Immutable;
using System.Text;

namespace NexusPipeline.Platform.Storage;

internal static class ApplicationTree
{
    internal const string HostEntry = "NexusPipeline.exe";
    internal const string LauncherEntry = "resources/desktop/NexusPipeline.Launcher.exe";
    internal const string MaintenanceEntry = "NexusPipeline.update.exe";
    internal const string DesktopEntry = "resources/desktop/NexusPipeline.Desktop.exe";
    internal const string DesktopArchive = "resources/desktop/resources/app.asar";
    internal const string ManifestPath = "resources/payload-manifest.json";
    internal static ImmutableArray<string> Roots { get; } =
        [HostEntry, MaintenanceEntry, "README.md", "resources"];
    private static readonly IComparer<byte[]> Utf8PathOrder = Comparer<byte[]>.Create((left, right)
        => left.AsSpan().SequenceCompareTo(right));

    internal static FileTreeSnapshot Capture(FileTreeScanner scanner, string requestedRoot, CancellationToken cancellation)
    {
        var root = PhysicalPathPolicy.NormalizeWithoutLinks(requestedRoot);
        if ((File.GetAttributes(root) & FileAttributes.Directory) == 0) throw Changed();
        var entries = new List<FileTreeEntry> { new("", FileObjectKind.Directory, 0, null) };
        foreach (var name in Roots)
        {
            var child = scanner.Capture(Path.Combine(root, name), cancellation);
            foreach (var entry in child.Entries)
                entries.Add(entry with { RelativePath = entry.RelativePath.Length == 0 ? name : name + "/" + entry.RelativePath });
        }
        var result = new FileTreeSnapshot(FileObjectKind.Directory,
            Array.AsReadOnly(entries.OrderBy(entry => Encoding.UTF8.GetBytes(entry.RelativePath), Utf8PathOrder).ToArray()));
        Validate(result, complete: false);
        return result;
    }

    internal static void Validate(FileTreeSnapshot snapshot, bool complete)
    {
        if (snapshot.Shape != FileObjectKind.Directory || snapshot.Entries.Count > 4096 ||
            snapshot.Entries.Where(entry => entry.Kind == FileObjectKind.File).Sum(entry => entry.SizeBytes) > 1073741824 ||
            snapshot.Entries.Any(entry => entry.RelativePath.Length != 0 && !Roots.Contains(entry.RelativePath.Split('/')[0], StringComparer.Ordinal)))
            throw Changed();
        foreach (var name in Roots)
        {
            var entry = snapshot.Entries.SingleOrDefault(entry => entry.RelativePath == name);
            if (entry is null ? complete : entry.Kind != (name == "resources" ? FileObjectKind.Directory : FileObjectKind.File))
                throw Changed();
        }
    }

    internal static ArchivedFileTree Slice(ArchivedFileTree tree, string name, IReadOnlyList<PhysicalTreeObject>? objects = null)
    {
        if (!Roots.Contains(name, StringComparer.Ordinal)) throw Changed();
        var root = tree.Snapshot.Entries.SingleOrDefault(entry => entry.RelativePath == name);
        if (root is null) return new(new(FileObjectKind.Missing, []), [], []);
        bool Inside(string path) => path == name || path.StartsWith(name + "/", StringComparison.Ordinal);
        string Relative(string path) => path.Length == name.Length ? "" : path[(name.Length + 1)..];
        var entries = tree.Snapshot.Entries.Where(entry => Inside(entry.RelativePath))
            .Select(entry => entry with { RelativePath = Relative(entry.RelativePath) }).ToArray();
        var identities = (objects ?? tree.OriginalObjects).Where(item => Inside(item.RelativePath))
            .Select(item => item with { RelativePath = Relative(item.RelativePath) }).ToArray();
        var digests = entries.Where(entry => entry.Kind == FileObjectKind.File).Select(entry => entry.Sha256).ToHashSet(StringComparer.Ordinal);
        var contents = tree.Contents.Where(blob => digests.Contains(blob.Sha256)).ToArray();
        var result = new ArchivedFileTree(new(root.Kind, Array.AsReadOnly(entries)), Array.AsReadOnly(identities), Array.AsReadOnly(contents));
        FileTreeArchive.ValidateArchive(result);
        return result;
    }

    internal static ArchivedFileTree Installed(FrozenPayloadApply plan, IReadOnlyList<PhysicalTreeObject> objects)
    {
        Validate(plan.Candidate.Snapshot, complete: true);
        var identity = plan.Before.OriginalIdentity ?? throw Changed();
        return plan.Candidate with { OriginalObjects = Array.AsReadOnly(objects.Select(item => item.RelativePath.Length == 0
            ? item with { Identity = identity } : item).ToArray()) };
    }

    internal static IOException Changed() => new("maintenance_payload_tree_changed");
}

internal enum OriginalCopy { Missing, Installed, Preserved }
internal enum CandidateCopy { None, Ready, Installed, Retired }

internal sealed record ApplicationRootState(string Name, ArchivedFileTree Original, ArchivedFileTree Candidate,
    OriginalCopy OriginalPlace, CandidateCopy CandidatePlace);

internal sealed class ApplicationScene : IDisposable
{
    private readonly FileTreeScanner _scanner;
    private readonly List<IDisposable> _held = [];
    private (string Name, ArchivedFileTree Original, ArchivedFileTree Candidate)[] _trees = [];
    internal FrozenPayloadApply Plan { get; }
    internal PayloadTreeScene Saved { get; }
    internal string Work { get; }
    internal string CandidateRoot => Path.Combine(Work, "candidate");
    internal PhysicalObjectIdentity RootIdentity { get; }
    internal PhysicalObjectIdentity? CandidateIdentity { get; private set; }

    private ApplicationScene(FileTreeScanner scanner, FrozenPayloadApply plan, PayloadTreeScene saved)
    {
        _scanner = scanner; Plan = plan; Saved = saved; Work = PayloadWorkspacePath.For(plan);
        RootIdentity = plan.Before.OriginalIdentity ?? throw ApplicationTree.Changed();
    }

    internal static ApplicationScene Acquire(FileTreeScanner scanner, FrozenPayloadApply plan, PayloadTreeScene saved,
        out IReadOnlyList<ApplicationRootState> observed)
    {
        var result = new ApplicationScene(scanner, plan, saved);
        try
        {
            result._held.Add(StableDirectoryLease.Acquire(Path.GetDirectoryName(plan.TargetPath)!));
            result._held.Add(StableDirectoryLease.Acquire(plan.TargetPath));
            RequireRoot(plan);
            if (saved.OperationId != plan.OperationId || saved.JournalId != plan.JournalId || saved.WorkspaceIdentity is null)
                throw ApplicationTree.Changed();
            result._held.Add(StableDirectoryLease.Acquire(result.Work));
            if (PhysicalObjectIdentity.Read(result.Work) != saved.WorkspaceIdentity ||
                saved.WorkspaceIdentity.VolumeSerial != plan.ParentIdentity.VolumeSerial) throw ApplicationTree.Changed();
            if (!saved.CandidateObjects.IsDefaultOrEmpty)
            {
                result.CandidateIdentity = saved.CandidateObjects.Single(item => item.RelativePath.Length == 0).Identity;
                result._held.Add(StableDirectoryLease.Acquire(result.CandidateRoot));
                if (PhysicalObjectIdentity.Read(result.CandidateRoot) != result.CandidateIdentity) throw ApplicationTree.Changed();
            }
            else result.RequireMissing(result.CandidateRoot);
            result._trees = ApplicationTree.Roots.Select(name => (name, ApplicationTree.Slice(plan.Before, name),
                ApplicationTree.Slice(plan.Candidate, name, saved.CandidateObjects))).ToArray();
            observed = result.Read();
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal static void RequireRoot(FrozenPayloadApply plan)
    {
        if (plan.Kind != JournalKind.UpdateApply || plan.Action != PayloadAction.Upgrade ||
            PhysicalPathPolicy.NormalizeWithoutLinks(plan.TargetPath) != plan.TargetPath ||
            PhysicalObjectIdentity.Read(Path.GetDirectoryName(plan.TargetPath)!) != plan.ParentIdentity ||
            plan.Before.OriginalIdentity is null || PhysicalObjectIdentity.Read(plan.TargetPath) != plan.Before.OriginalIdentity)
            throw ApplicationTree.Changed();
        ApplicationTree.Validate(plan.Before.Snapshot, complete: false);
        ApplicationTree.Validate(plan.Candidate.Snapshot, complete: true);
    }

    internal IReadOnlyList<ApplicationRootState> Read()
    {
        RequireRoot(Plan);
        RequireNamespace();
        return Array.AsReadOnly(_trees.Select(root => ReadRoot(root.Name, root.Original, root.Candidate)).ToArray());
    }

    private ApplicationRootState ReadRoot(string name, ArchivedFileTree original, ArchivedFileTree candidate)
    {
        var installed = Target(name); var before = Before(name); var ready = Ready(name); var spent = Spent(name);
        FileTreeSnapshot? installedTree = null;
        IReadOnlyList<PhysicalTreeObject>? installedObjects = null;
        bool MatchesInstalled(ArchivedFileTree expected)
        {
            var actual = installedTree ??= _scanner.Capture(installed, default);
            return SameTree(actual, expected.Snapshot) &&
                (installedObjects ??= ReadIdentities(installed, actual)).SequenceEqual(expected.OriginalObjects);
        }
        var originalHere = original.Snapshot.Shape != FileObjectKind.Missing && MatchesInstalled(original);
        var preserved = original.Snapshot.Shape != FileObjectKind.Missing && Matches(before, original);
        if (original.Snapshot.Shape != FileObjectKind.Missing && originalHere == preserved) throw ApplicationTree.Changed();
        if (!preserved) RequireMissing(before);
        var candidateReady = CandidateIdentity is not null && Matches(ready, candidate);
        var candidateHere = CandidateIdentity is not null && MatchesInstalled(candidate);
        var retired = CandidateIdentity is not null && Matches(spent, candidate);
        if (CandidateIdentity is not null && (candidateReady ? 1 : 0) + (candidateHere ? 1 : 0) + (retired ? 1 : 0) != 1 ||
            originalHere && candidateHere) throw ApplicationTree.Changed();
        if (!candidateReady) RequireMissing(ready);
        if (!retired) RequireMissing(spent);
        if (!originalHere && !candidateHere) RequireMissing(installed);
        return new(name, original, candidate,
            originalHere ? OriginalCopy.Installed : preserved ? OriginalCopy.Preserved : OriginalCopy.Missing,
            candidateReady ? CandidateCopy.Ready : candidateHere ? CandidateCopy.Installed : retired ? CandidateCopy.Retired : CandidateCopy.None);
    }

    internal string Target(string name) => Path.Combine(Plan.TargetPath, name);
    internal string Before(string name) => Path.Combine(Work, "before-" + name);
    internal string Ready(string name) => Path.Combine(CandidateRoot, name);
    internal string Spent(string name) => Path.Combine(Work, "spent-" + name);

    internal void RequireAt(string path, ArchivedFileTree tree)
    {
        if (!Matches(path, tree)) throw ApplicationTree.Changed();
    }

    private bool Matches(string path, ArchivedFileTree tree)
    {
        var actual = _scanner.Capture(path, default);
        return SameTree(actual, tree.Snapshot) && ReadIdentities(path, actual).SequenceEqual(tree.OriginalObjects);
    }

    private static bool SameTree(FileTreeSnapshot actual, FileTreeSnapshot expected)
        => actual.Shape == expected.Shape && actual.Entries.SequenceEqual(expected.Entries);

    private static IReadOnlyList<PhysicalTreeObject> ReadIdentities(string path, FileTreeSnapshot actual)
    {
        PhysicalTreeIdentity.RequireSingleLinks(path, actual);
        return PhysicalTreeIdentity.Capture(path, actual);
    }

    private void RequireMissing(string path)
    {
        if (_scanner.Capture(path, default).Shape != FileObjectKind.Missing) throw ApplicationTree.Changed();
    }

    private void RequireNamespace()
    {
        if (PhysicalObjectIdentity.Read(Work) != Saved.WorkspaceIdentity) throw ApplicationTree.Changed();
        var names = new Dictionary<string, FileObjectKind>(StringComparer.Ordinal) { ["candidate"] = FileObjectKind.Directory };
        foreach (var (name, original, candidate) in _trees)
        {
            if (original.Snapshot.Shape != FileObjectKind.Missing) names.Add("before-" + name, original.Snapshot.Shape);
            names.Add("spent-" + name, candidate.Snapshot.Shape);
        }
        foreach (var path in Directory.EnumerateFileSystemEntries(Work))
        {
            PhysicalPathPolicy.NormalizeWithoutLinks(path);
            var kind = (File.GetAttributes(path) & FileAttributes.Directory) != 0 ? FileObjectKind.Directory : FileObjectKind.File;
            if (!names.TryGetValue(Path.GetFileName(path), out var expected) || kind != expected) throw ApplicationTree.Changed();
        }
        if (CandidateIdentity is not null)
            foreach (var path in Directory.EnumerateFileSystemEntries(CandidateRoot))
            {
                PhysicalPathPolicy.NormalizeWithoutLinks(path);
                if (!ApplicationTree.Roots.Contains(Path.GetFileName(path), StringComparer.Ordinal)) throw ApplicationTree.Changed();
            }
    }

    public void Dispose()
    {
        for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
        _held.Clear();
    }
}

internal sealed class ApplicationTreeEffects(FileTreeScanner scanner, FileTreeArchive archive) : IPayloadRestoration
{
    internal PhysicalObjectIdentity CreateWorkspace(FrozenPayloadApply plan)
    {
        using var parent = StableDirectoryLease.Acquire(Path.GetDirectoryName(plan.TargetPath)!);
        using var root = StableDirectoryLease.Acquire(plan.TargetPath);
        ApplicationScene.RequireRoot(plan);
        RequireOriginal(plan);
        var work = PayloadWorkspacePath.For(plan);
        if (scanner.Capture(work, default).Shape != FileObjectKind.Missing) throw ApplicationTree.Changed();
        ExclusiveDirectory.Create(work);
        var identity = PhysicalObjectIdentity.Read(work);
        if (identity.VolumeSerial != plan.ParentIdentity.VolumeSerial) throw ApplicationTree.Changed();
        return identity;
    }

    internal ImmutableArray<PhysicalTreeObject> MaterializeCandidate(FrozenPayloadApply plan,
        PayloadTreeScene saved, CancellationToken cancellation)
    {
        if (saved.Stage != "workspace-created") throw ApplicationTree.Changed();
        using var scene = ApplicationScene.Acquire(scanner, plan, saved, out var roots);
        if (roots.Any(item => item.OriginalPlace == OriginalCopy.Preserved || item.CandidatePlace != CandidateCopy.None))
            throw ApplicationTree.Changed();
        _ = archive.MaterializeNew(plan.Candidate, scene.CandidateRoot, cancellation);
        var actual = scanner.Capture(scene.CandidateRoot, cancellation);
        if (actual.Shape != plan.Candidate.Snapshot.Shape || !actual.Entries.SequenceEqual(plan.Candidate.Snapshot.Entries))
            throw ApplicationTree.Changed();
        PhysicalTreeIdentity.RequireSingleLinks(scene.CandidateRoot, actual);
        return PhysicalTreeIdentity.Capture(scene.CandidateRoot, actual).ToImmutableArray();
    }

    internal void PreserveOriginal(FrozenPayloadApply plan, PayloadTreeScene saved)
    {
        if (saved.Stage != "original-preservation-started") throw ApplicationTree.Changed();
        using var scene = ApplicationScene.Acquire(scanner, plan, saved, out var roots);
        if (roots.Any(item => item.CandidatePlace != CandidateCopy.Ready)) throw ApplicationTree.Changed();
        foreach (var item in roots.Where(item => item.OriginalPlace == OriginalCopy.Installed))
        {
            Move(scene.Target(item.Name), scene.RootIdentity, item.Original, scene.Before(item.Name), saved.WorkspaceIdentity!);
            scene.RequireAt(scene.Before(item.Name), item.Original);
        }
        if (scene.Read().Any(item => item.OriginalPlace == OriginalCopy.Installed)) throw ApplicationTree.Changed();
    }

    internal void InstallCandidate(FrozenPayloadApply plan, PayloadTreeScene saved)
    {
        if (saved.Stage != "original-preserved") throw ApplicationTree.Changed();
        using var scene = ApplicationScene.Acquire(scanner, plan, saved, out var roots);
        if (roots.Any(item => item.OriginalPlace == OriginalCopy.Installed ||
            item.CandidatePlace is not (CandidateCopy.Ready or CandidateCopy.Installed))) throw ApplicationTree.Changed();
        foreach (var item in roots.Where(item => item.CandidatePlace == CandidateCopy.Ready))
        {
            Move(scene.Ready(item.Name), scene.CandidateIdentity!, item.Candidate, scene.Target(item.Name), scene.RootIdentity);
            scene.RequireAt(scene.Target(item.Name), item.Candidate);
        }
        if (scene.Read().Any(item => item.CandidatePlace != CandidateCopy.Installed)) throw ApplicationTree.Changed();
    }

    internal void RetireCandidate(FrozenPayloadApply plan, PayloadTreeScene saved)
    {
        using var scene = ApplicationScene.Acquire(scanner, plan, saved, out var roots);
        if (scene.CandidateIdentity is null) throw ApplicationTree.Changed();
        foreach (var item in roots.Where(item => item.CandidatePlace == CandidateCopy.Installed))
        {
            Move(scene.Target(item.Name), scene.RootIdentity, item.Candidate, scene.Spent(item.Name), saved.WorkspaceIdentity!);
            scene.RequireAt(scene.Spent(item.Name), item.Candidate);
        }
        if (scene.Read().Any(item => item.CandidatePlace == CandidateCopy.Installed)) throw ApplicationTree.Changed();
    }

    internal void RestoreOriginal(FrozenPayloadApply plan, PayloadTreeScene saved)
    {
        using var scene = ApplicationScene.Acquire(scanner, plan, saved, out var roots);
        if (roots.Any(item => item.CandidatePlace == CandidateCopy.Installed)) throw ApplicationTree.Changed();
        foreach (var item in roots.Where(item => item.OriginalPlace == OriginalCopy.Preserved))
        {
            Move(scene.Before(item.Name), saved.WorkspaceIdentity!, item.Original, scene.Target(item.Name), scene.RootIdentity);
            scene.RequireAt(scene.Target(item.Name), item.Original);
        }
        if (scene.Read().Any(item => item.OriginalPlace == OriginalCopy.Preserved)) throw ApplicationTree.Changed();
    }

    internal IDisposable HoldInstalled(FrozenPayloadApply plan, PayloadTreeScene saved)
    {
        var held = new HeldStorageResources();
        try
        {
            var scene = ApplicationScene.Acquire(scanner, plan, saved, out var roots); held.Add(scene);
            if (roots.Any(item => item.CandidatePlace != CandidateCopy.Installed || item.OriginalPlace == OriginalCopy.Installed))
                throw ApplicationTree.Changed();
            held.Add(TreeReadLease.AcquireApplication(plan.TargetPath, plan.ParentIdentity, ApplicationTree.Installed(plan, saved.CandidateObjects)));
            foreach (var item in roots)
                held.Add(TreeReadLease.Acquire(scene.Before(item.Name), saved.WorkspaceIdentity!, item.Original));
            return held;
        }
        catch (Exception error) { held.Release(error); throw; }
    }

    internal (bool RetireCandidate, bool RestoreOriginal) ObserveRestoration(FrozenPayloadApply plan, PayloadTreeScene saved)
    {
        if (saved.WorkspaceIdentity is null)
        {
            using var parent = StableDirectoryLease.Acquire(Path.GetDirectoryName(plan.TargetPath)!);
            using var root = StableDirectoryLease.Acquire(plan.TargetPath);
            ApplicationScene.RequireRoot(plan);
            if (saved.OperationId != plan.OperationId || saved.JournalId != plan.JournalId ||
                scanner.Capture(PayloadWorkspacePath.For(plan), default).Shape != FileObjectKind.Missing) throw ApplicationTree.Changed();
            RequireOriginal(plan);
            return (false, false);
        }
        using var scene = ApplicationScene.Acquire(scanner, plan, saved, out var roots);
        return (roots.Any(item => item.CandidatePlace == CandidateCopy.Installed), roots.Any(item => item.OriginalPlace == OriginalCopy.Preserved));
    }

    internal RestoredPayloadLease HoldRestored(FrozenPayloadApply plan, PayloadTreeScene saved) =>
        RestoredPayloadLease.Acquire(this, plan, saved);

    (bool RetireCandidate, bool RestoreOriginal) IPayloadRestoration.ObserveRestoration(FrozenPayloadApply plan,
        PayloadTreeScene saved) => ObserveRestoration(plan, saved);

    IDisposable IPayloadRestoration.HoldRestoredCore(FrozenPayloadApply plan, PayloadTreeScene saved)
    {
        if (ObserveRestoration(plan, saved) != (false, false)) throw ApplicationTree.Changed();
        var held = new HeldStorageResources();
        try
        {
            held.Add(TreeReadLease.AcquireApplication(plan.TargetPath, plan.ParentIdentity, plan.Before));
            if (saved.WorkspaceIdentity is not null)
            {
                var scene = ApplicationScene.Acquire(scanner, plan, saved, out var roots); held.Add(scene);
                foreach (var item in roots)
                {
                    if (item.CandidatePlace == CandidateCopy.Ready)
                        held.Add(TreeReadLease.Acquire(scene.Ready(item.Name), scene.CandidateIdentity!, item.Candidate));
                    else if (item.CandidatePlace == CandidateCopy.Retired)
                        held.Add(TreeReadLease.Acquire(scene.Spent(item.Name), saved.WorkspaceIdentity, item.Candidate));
                    else if (item.CandidatePlace != CandidateCopy.None) throw ApplicationTree.Changed();
                }
            }
            return held;
        }
        catch (Exception error) { held.Release(error); throw; }
    }

    private void RequireOriginal(FrozenPayloadApply plan)
    {
        var actual = ApplicationTree.Capture(scanner, plan.TargetPath, default);
        if (!actual.Entries.SequenceEqual(plan.Before.Snapshot.Entries)) throw ApplicationTree.Changed();
        PhysicalTreeIdentity.RequireSingleLinks(plan.TargetPath, actual);
        if (!PhysicalTreeIdentity.Capture(plan.TargetPath, actual).SequenceEqual(plan.Before.OriginalObjects)) throw ApplicationTree.Changed();
    }

    private static void Move(string source, PhysicalObjectIdentity parent, ArchivedFileTree tree,
        string destination, PhysicalObjectIdentity destinationParent)
    {
        PhysicalObjectMove.MoveVerified(new(source, parent, tree.OriginalIdentity ?? throw ApplicationTree.Changed(),
            destination, destinationParent, tree.Snapshot.Shape));
    }

}
