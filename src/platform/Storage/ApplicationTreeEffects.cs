using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage;

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
        using var scene = ApplicationScene.Acquire(scanner, plan, saved);
        if (scene.Read().Any(item => item.OriginalPlace == OriginalCopy.Preserved || item.CandidatePlace != CandidateCopy.None))
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
        using var scene = ApplicationScene.Acquire(scanner, plan, saved);
        var roots = scene.Read();
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
        using var scene = ApplicationScene.Acquire(scanner, plan, saved);
        var roots = scene.Read();
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
        using var scene = ApplicationScene.Acquire(scanner, plan, saved);
        if (scene.CandidateIdentity is null) throw ApplicationTree.Changed();
        foreach (var item in scene.Read().Where(item => item.CandidatePlace == CandidateCopy.Installed))
        {
            Move(scene.Target(item.Name), scene.RootIdentity, item.Candidate, scene.Spent(item.Name), saved.WorkspaceIdentity!);
            scene.RequireAt(scene.Spent(item.Name), item.Candidate);
        }
        if (scene.Read().Any(item => item.CandidatePlace == CandidateCopy.Installed)) throw ApplicationTree.Changed();
    }

    internal void RestoreOriginal(FrozenPayloadApply plan, PayloadTreeScene saved)
    {
        using var scene = ApplicationScene.Acquire(scanner, plan, saved);
        var roots = scene.Read();
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
        var held = new HeldTrees();
        try
        {
            var scene = ApplicationScene.Acquire(scanner, plan, saved); held.Add(scene);
            var roots = scene.Read();
            if (roots.Any(item => item.CandidatePlace != CandidateCopy.Installed || item.OriginalPlace == OriginalCopy.Installed))
                throw ApplicationTree.Changed();
            held.Add(TreeReadLease.AcquireApplication(plan.TargetPath, plan.ParentIdentity, ApplicationTree.Installed(plan, saved.CandidateObjects)));
            foreach (var item in roots)
                held.Add(TreeReadLease.Acquire(scene.Before(item.Name), saved.WorkspaceIdentity!, item.Original));
            return held;
        }
        catch { held.Dispose(); throw; }
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
        using var scene = ApplicationScene.Acquire(scanner, plan, saved);
        var roots = scene.Read();
        return (roots.Any(item => item.CandidatePlace == CandidateCopy.Installed), roots.Any(item => item.OriginalPlace == OriginalCopy.Preserved));
    }

    internal RestoredPayloadLease HoldRestored(FrozenPayloadApply plan, PayloadTreeScene saved) =>
        RestoredPayloadLease.Acquire(this, plan, saved);

    (bool RetireCandidate, bool RestoreOriginal) IPayloadRestoration.ObserveRestoration(FrozenPayloadApply plan,
        PayloadTreeScene saved) => ObserveRestoration(plan, saved);

    IDisposable IPayloadRestoration.HoldRestoredCore(FrozenPayloadApply plan, PayloadTreeScene saved)
    {
        if (ObserveRestoration(plan, saved) != (false, false)) throw ApplicationTree.Changed();
        var held = new HeldTrees();
        try
        {
            held.Add(TreeReadLease.AcquireApplication(plan.TargetPath, plan.ParentIdentity, plan.Before));
            if (saved.WorkspaceIdentity is not null)
            {
                var scene = ApplicationScene.Acquire(scanner, plan, saved); held.Add(scene);
                foreach (var item in scene.Read())
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
        catch { held.Dispose(); throw; }
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

    private sealed class HeldTrees : IDisposable
    {
        private readonly List<IDisposable> _held = [];
        internal void Add(IDisposable item) => _held.Add(item);
        public void Dispose()
        {
            for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
            _held.Clear();
        }
    }
}
