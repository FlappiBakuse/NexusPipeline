using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage;

internal sealed class PayloadTreeEffects(FileTreeScanner scanner, FileTreeArchive archive) : IPayloadRestoration
{
    internal PhysicalObjectIdentity CreateWorkspace(FrozenPayloadApply plan)
    {
        RequireParent(plan);
        using var parent = StableDirectoryLease.Acquire(Path.GetDirectoryName(plan.TargetPath)!);
        RequireTree(plan.TargetPath, plan.Before.Snapshot, plan.Before.OriginalObjects);
        var work = PayloadWorkspacePath.For(plan);
        RequireMissing(work); ExclusiveDirectory.Create(work);
        var identity = PhysicalObjectIdentity.Read(work);
        if (identity.VolumeSerial != plan.ParentIdentity.VolumeSerial) throw Invalid();
        return identity;
    }

    internal ImmutableArray<PhysicalTreeObject> MaterializeCandidate(FrozenPayloadApply plan,
        PayloadTreeScene previous, CancellationToken cancellation)
    {
        if (previous.Stage != "workspace-created" || plan.Action == PayloadAction.Remove) throw Invalid();
        var work = PayloadWorkspacePath.For(plan);
        using var held = StableDirectoryLease.Acquire(work);
        RequireWorkspace(plan, previous, []);
        RequireTree(plan.TargetPath, plan.Before.Snapshot, plan.Before.OriginalObjects);
        var candidate = Path.Combine(work, "candidate"); RequireMissing(candidate);
        archive.MaterializePackage(plan, candidate, cancellation);
        var actual = scanner.Capture(candidate, cancellation);
        if (actual.Shape != plan.Candidate.Snapshot.Shape || !actual.Entries.SequenceEqual(plan.Candidate.Snapshot.Entries)) throw Invalid();
        PhysicalTreeIdentity.RequireSingleLinks(candidate, actual);
        return PhysicalTreeIdentity.Capture(candidate, actual).ToImmutableArray();
    }

    internal void PreserveOriginal(FrozenPayloadApply plan, PayloadTreeScene previous)
    {
        if (plan.Before.Snapshot.Shape != FileObjectKind.Directory ||
            (plan.Action == PayloadAction.Upgrade ? previous.Stage != "original-preservation-started" :
                plan.Action != PayloadAction.Remove || previous.Stage != "workspace-created")) throw Invalid();
        var work = PayloadWorkspacePath.For(plan);
        using var held = StableDirectoryLease.Acquire(work);
        RequireWorkspace(plan, previous, plan.Action == PayloadAction.Remove ? ["before"] : ["candidate", "before"]);
        if (plan.Action != PayloadAction.Remove)
            RequireTree(Path.Combine(work, "candidate"), plan.Candidate.Snapshot, previous.CandidateObjects);
        var before = Path.Combine(work, "before");
        if (Matches(before, plan.Before.Snapshot, plan.Before.OriginalObjects) && IsMissing(plan.TargetPath)) return;
        RequireTree(plan.TargetPath, plan.Before.Snapshot, plan.Before.OriginalObjects); RequireMissing(before);
        PhysicalObjectMove.MoveVerified(new(plan.TargetPath, plan.ParentIdentity, plan.Before.OriginalIdentity!,
            before, previous.WorkspaceIdentity!, FileObjectKind.Directory));
        RequireTree(before, plan.Before.Snapshot, plan.Before.OriginalObjects); RequireMissing(plan.TargetPath);
    }

    internal void InstallCandidate(FrozenPayloadApply plan, PayloadTreeScene previous)
    {
        if (plan.Action == PayloadAction.Remove || previous.Stage !=
            (plan.Before.Snapshot.Shape == FileObjectKind.Missing ? "candidate-materialized" : "original-preserved")) throw Invalid();
        var work = PayloadWorkspacePath.For(plan);
        using var held = StableDirectoryLease.Acquire(work);
        RequireWorkspace(plan, previous, ["candidate", "before"]);
        RequireTree(Path.Combine(work, "before"), plan.Before.Snapshot, plan.Before.OriginalObjects);
        var candidate = Path.Combine(work, "candidate");
        if (Matches(plan.TargetPath, plan.Candidate.Snapshot, previous.CandidateObjects) && IsMissing(candidate)) return;
        RequireMissing(plan.TargetPath); RequireTree(candidate, plan.Candidate.Snapshot, previous.CandidateObjects);
        var identity = previous.CandidateObjects.Single(item => item.RelativePath.Length == 0).Identity;
        PhysicalObjectMove.MoveVerified(new(candidate, previous.WorkspaceIdentity!, identity,
            plan.TargetPath, plan.ParentIdentity, FileObjectKind.Directory));
        RequireTree(plan.TargetPath, plan.Candidate.Snapshot, previous.CandidateObjects); RequireMissing(candidate);
    }

    internal void RequireRemoved(FrozenPayloadApply plan, PayloadTreeScene previous)
    {
        if (plan.Action != PayloadAction.Remove || previous.Stage !=
            (plan.Before.Snapshot.Shape == FileObjectKind.Missing ? "workspace-created" : "original-preserved")) throw Invalid();
        var work = PayloadWorkspacePath.For(plan);
        using var held = StableDirectoryLease.Acquire(work);
        RequireWorkspace(plan, previous, ["before"]);
        RequireTree(Path.Combine(work, "before"), plan.Before.Snapshot, plan.Before.OriginalObjects);
        RequireMissing(plan.TargetPath);
    }

    internal void RetireCandidate(FrozenPayloadApply plan, PayloadTreeScene saved)
    {
        if (plan.Action == PayloadAction.Remove || saved.CandidateObjects.IsDefaultOrEmpty) throw Invalid();
        var work = PayloadWorkspacePath.For(plan);
        using var held = StableDirectoryLease.Acquire(work);
        RequireWorkspace(plan, saved, ["candidate", "before", "spent"]);
        var before = Path.Combine(work, "before"); var candidate = Path.Combine(work, "candidate"); var spent = Path.Combine(work, "spent");
        if (Matches(plan.TargetPath, plan.Before.Snapshot, plan.Before.OriginalObjects) && IsMissing(before)) return;
        RequireTree(before, plan.Before.Snapshot, plan.Before.OriginalObjects);
        if (IsMissing(plan.TargetPath))
        {
            var ready = Matches(candidate, plan.Candidate.Snapshot, saved.CandidateObjects);
            var retired = Matches(spent, plan.Candidate.Snapshot, saved.CandidateObjects);
            if (ready && IsMissing(spent) || retired && IsMissing(candidate)) return;
            throw Invalid();
        }
        RequireMissing(candidate); RequireMissing(spent);
        RequireTree(plan.TargetPath, plan.Candidate.Snapshot, saved.CandidateObjects);
        var identity = saved.CandidateObjects.Single(item => item.RelativePath.Length == 0).Identity;
        PhysicalObjectMove.MoveVerified(new(plan.TargetPath, plan.ParentIdentity, identity,
            spent, saved.WorkspaceIdentity!, FileObjectKind.Directory));
        RequireTree(spent, plan.Candidate.Snapshot, saved.CandidateObjects); RequireMissing(plan.TargetPath);
    }

    internal void RestoreOriginal(FrozenPayloadApply plan, PayloadTreeScene saved)
    {
        var work = PayloadWorkspacePath.For(plan);
        using var held = StableDirectoryLease.Acquire(work);
        RequireWorkspace(plan, saved, ["candidate", "before", "spent"]);
        var before = Path.Combine(work, "before");
        if (Matches(plan.TargetPath, plan.Before.Snapshot, plan.Before.OriginalObjects) && IsMissing(before)) return;
        RequireMissing(plan.TargetPath); RequireTree(before, plan.Before.Snapshot, plan.Before.OriginalObjects);
        if (plan.Before.Snapshot.Shape == FileObjectKind.Missing) return;
        PhysicalObjectMove.MoveVerified(new(before, saved.WorkspaceIdentity!, plan.Before.OriginalIdentity!,
            plan.TargetPath, plan.ParentIdentity, FileObjectKind.Directory));
        RequireTree(plan.TargetPath, plan.Before.Snapshot, plan.Before.OriginalObjects); RequireMissing(before);
    }

    internal IDisposable HoldInstalled(FrozenPayloadApply plan, PayloadTreeScene saved)
    {
        var result = new HeldTrees();
        try
        {
            var work = PayloadWorkspacePath.For(plan);
            result.Add(StableDirectoryLease.Acquire(work)); RequireWorkspace(plan, saved, ["before"]);
            var target = plan.Action == PayloadAction.Remove
                ? new ArchivedFileTree(new(FileObjectKind.Missing, []), [], [])
                : plan.Candidate with { OriginalObjects = saved.CandidateObjects };
            result.Add(TreeReadLease.Acquire(plan.TargetPath, plan.ParentIdentity, target));
            result.Add(TreeReadLease.Acquire(Path.Combine(work, "before"), saved.WorkspaceIdentity!, plan.Before));
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal (bool RetireCandidate, bool RestoreOriginal) ObserveRestoration(FrozenPayloadApply plan,
        PayloadTreeScene saved)
    {
        RequireParent(plan);
        var work = PayloadWorkspacePath.For(plan);
        if (saved.WorkspaceIdentity is null)
        {
            RequireMissing(work); RequireTree(plan.TargetPath, plan.Before.Snapshot, plan.Before.OriginalObjects);
            return (false, false);
        }
        using var held = StableDirectoryLease.Acquire(work);
        RequireWorkspace(plan, saved, ["candidate", "before", "spent"]);
        var before = Path.Combine(work, "before"); var candidate = Path.Combine(work, "candidate"); var spent = Path.Combine(work, "spent");
        var originalHere = Matches(plan.TargetPath, plan.Before.Snapshot, plan.Before.OriginalObjects);
        var hasCandidate = !saved.CandidateObjects.IsEmpty;
        var candidateHere = hasCandidate && Matches(plan.TargetPath, plan.Candidate.Snapshot, saved.CandidateObjects);
        var candidateReady = hasCandidate && Matches(candidate, plan.Candidate.Snapshot, saved.CandidateObjects);
        var candidateRetired = hasCandidate && Matches(spent, plan.Candidate.Snapshot, saved.CandidateObjects);
        if (!candidateReady) RequireMissing(candidate);
        if (!candidateRetired) RequireMissing(spent);
        if (hasCandidate ? (candidateHere ? 1 : 0) + (candidateReady ? 1 : 0) + (candidateRetired ? 1 : 0) != 1 :
            !IsMissing(candidate) || !IsMissing(spent)) throw Invalid();
        if (originalHere)
        {
            RequireMissing(before);
            if (candidateHere) throw Invalid();
            return (false, false);
        }
        if (!candidateHere) RequireMissing(plan.TargetPath);
        RequireTree(before, plan.Before.Snapshot, plan.Before.OriginalObjects);
        return (candidateHere, plan.Before.Snapshot.Shape != FileObjectKind.Missing);
    }

    internal RestoredPayloadLease HoldRestored(FrozenPayloadApply plan, PayloadTreeScene saved) =>
        RestoredPayloadLease.Acquire(this, plan, saved);

    IDisposable IPayloadRestoration.HoldRestoredCore(FrozenPayloadApply plan, PayloadTreeScene saved) => HoldRestoredCore(plan, saved);

    (bool RetireCandidate, bool RestoreOriginal) IPayloadRestoration.ObserveRestoration(FrozenPayloadApply plan,
        PayloadTreeScene saved) => ObserveRestoration(plan, saved);

    private IDisposable HoldRestoredCore(FrozenPayloadApply plan, PayloadTreeScene saved)
    {
        var actual = ObserveRestoration(plan, saved);
        if (actual != (false, false)) throw Invalid();
        var held = new HeldTrees();
        try
        {
            held.Add(StableDirectoryLease.Acquire(Path.GetDirectoryName(plan.TargetPath)!));
            held.Add(TreeReadLease.Acquire(plan.TargetPath, plan.ParentIdentity, plan.Before));
            var work = PayloadWorkspacePath.For(plan);
            if (saved.WorkspaceIdentity is not null)
            {
                held.Add(StableDirectoryLease.Acquire(work));
                foreach (var name in new[] { "candidate", "spent" })
                {
                    var path = Path.Combine(work, name);
                    if (!IsMissing(path))
                        held.Add(TreeReadLease.Acquire(path, saved.WorkspaceIdentity,
                            plan.Candidate with { OriginalObjects = saved.CandidateObjects }));
                }
            }
            return held;
        }
        catch { held.Dispose(); throw; }
    }

    private void RequireWorkspace(FrozenPayloadApply plan, PayloadTreeScene saved, IReadOnlyList<string> allowed)
    {
        RequireParent(plan);
        var work = PayloadWorkspacePath.For(plan);
        if (saved.OperationId != plan.OperationId || saved.JournalId != plan.JournalId ||
            saved.WorkspaceIdentity is null || PhysicalPathPolicy.NormalizeWithoutLinks(work) != work ||
            PhysicalObjectIdentity.Read(work) != saved.WorkspaceIdentity ||
            saved.WorkspaceIdentity.VolumeSerial != plan.ParentIdentity.VolumeSerial) throw Invalid();
        foreach (var entry in Directory.EnumerateFileSystemEntries(work))
        {
            PhysicalPathPolicy.NormalizeWithoutLinks(entry);
            if (!allowed.Contains(Path.GetFileName(entry), StringComparer.Ordinal) ||
                (File.GetAttributes(entry) & FileAttributes.Directory) == 0) throw Invalid();
        }
    }

    private void RequireParent(FrozenPayloadApply plan)
    {
        if (plan.Kind != JournalKind.ExtensionApply || PhysicalPathPolicy.NormalizeWithoutLinks(plan.TargetPath) != plan.TargetPath ||
            PhysicalObjectIdentity.Read(Path.GetDirectoryName(plan.TargetPath)!) != plan.ParentIdentity ||
            plan.Before.Snapshot.Shape is not (FileObjectKind.Missing or FileObjectKind.Directory) ||
            plan.Candidate.Snapshot.Shape != (plan.Action == PayloadAction.Remove ? FileObjectKind.Missing : FileObjectKind.Directory))
            throw Invalid();
    }

    private void RequireTree(string path, FileTreeSnapshot snapshot, IReadOnlyList<PhysicalTreeObject> objects)
    {
        if (!Matches(path, snapshot, objects)) throw Invalid();
    }
    private bool Matches(string path, FileTreeSnapshot snapshot, IReadOnlyList<PhysicalTreeObject> objects)
    {
        var actual = scanner.Capture(path, CancellationToken.None);
        if (actual.Shape != snapshot.Shape || !actual.Entries.SequenceEqual(snapshot.Entries)) return false;
        PhysicalTreeIdentity.RequireSingleLinks(path, actual);
        return PhysicalTreeIdentity.Capture(path, actual).SequenceEqual(objects);
    }
    private bool IsMissing(string path) => scanner.Capture(path, CancellationToken.None).Shape == FileObjectKind.Missing;
    private void RequireMissing(string path) { if (!IsMissing(path)) throw Invalid(); }
    private static IOException Invalid() => new("extension_payload_tree_changed");

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
