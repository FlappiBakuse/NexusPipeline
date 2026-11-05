using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage;

internal enum OriginalCopy { Missing, Installed, Preserved }
internal enum CandidateCopy { None, Ready, Installed, Retired }

internal sealed record ApplicationRootState(string Name, ArchivedFileTree Original, ArchivedFileTree Candidate,
    OriginalCopy OriginalPlace, CandidateCopy CandidatePlace);

internal sealed class ApplicationScene : IDisposable
{
    private readonly FileTreeScanner _scanner;
    private readonly List<IDisposable> _held = [];
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

    internal static ApplicationScene Acquire(FileTreeScanner scanner, FrozenPayloadApply plan, PayloadTreeScene saved)
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
            result.RequireNamespace();
            _ = result.Read();
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
        return Array.AsReadOnly(ApplicationTree.Roots.Select(ReadRoot).ToArray());
    }

    private ApplicationRootState ReadRoot(string name)
    {
        var original = ApplicationTree.Slice(Plan.Before, name);
        var candidate = ApplicationTree.Slice(Plan.Candidate, name, Saved.CandidateObjects);
        var installed = Target(name); var before = Before(name); var ready = Ready(name); var spent = Spent(name);
        var originalHere = original.Snapshot.Shape != FileObjectKind.Missing && Matches(installed, original);
        var preserved = original.Snapshot.Shape != FileObjectKind.Missing && Matches(before, original);
        if (original.Snapshot.Shape != FileObjectKind.Missing && originalHere == preserved) throw ApplicationTree.Changed();
        if (!preserved) RequireMissing(before);
        var candidateReady = CandidateIdentity is not null && Matches(ready, candidate);
        var candidateHere = CandidateIdentity is not null && Matches(installed, candidate);
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
        if (actual.Shape != tree.Snapshot.Shape || !actual.Entries.SequenceEqual(tree.Snapshot.Entries)) return false;
        PhysicalTreeIdentity.RequireSingleLinks(path, actual);
        return PhysicalTreeIdentity.Capture(path, actual).SequenceEqual(tree.OriginalObjects);
    }

    private void RequireMissing(string path)
    {
        if (_scanner.Capture(path, default).Shape != FileObjectKind.Missing) throw ApplicationTree.Changed();
    }

    private void RequireNamespace()
    {
        if (PhysicalObjectIdentity.Read(Work) != Saved.WorkspaceIdentity) throw ApplicationTree.Changed();
        var names = new Dictionary<string, FileObjectKind>(StringComparer.Ordinal) { ["candidate"] = FileObjectKind.Directory };
        foreach (var name in ApplicationTree.Roots)
        {
            var original = ApplicationTree.Slice(Plan.Before, name);
            if (original.Snapshot.Shape != FileObjectKind.Missing) names.Add("before-" + name, original.Snapshot.Shape);
            names.Add("spent-" + name, ApplicationTree.Slice(Plan.Candidate, name).Snapshot.Shape);
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
