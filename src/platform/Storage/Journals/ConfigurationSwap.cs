using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;

namespace NexusPipeline.Platform.Storage.Journals;

public sealed record ConfigurationWriterObservation(Guid InstanceId, Guid JournalId, bool IsComplete, WriterState State);
public sealed record PreparedConfigurationExchange(FrozenConfigurationExchange Plan, string WorkPath,
    PhysicalObjectIdentity WorkIdentity, IReadOnlyList<PhysicalTreeObject> CandidateObjects);

public sealed class ConfigurationSwap(FileTreeScanner scanner, FileTreeArchive archive)
{
    public PreparedConfigurationExchange Prepare(FrozenConfigurationExchange plan, ConfigurationWriterObservation writers, CancellationToken cancellationToken)
    {
        using var parentLease = StableDirectoryLease.Acquire(Path.GetDirectoryName(plan.TargetPath)!);
        VerifyWriters(plan, writers);
        VerifyParent(plan);
        VerifyTree(plan.TargetPath, plan.Before.Snapshot, plan.Before.OriginalObjects);
        var work = WorkPath(plan);
        ExclusiveDirectory.Create(work);
        var identity = PhysicalObjectIdentity.Read(work);
        var candidate = Path.Combine(work, "candidate");
        archive.MaterializeNew(plan.Candidate, candidate, cancellationToken);
        var objects = PhysicalTreeIdentity.Capture(candidate, plan.Candidate.Snapshot);
        return new(plan, work, identity, objects);
    }

    public void PreserveOriginal(PreparedConfigurationExchange prepared, ConfigurationWriterObservation writers)
    {
        using var pathLease = StableDirectoryLease.Acquire(prepared.WorkPath);
        VerifyPrepared(prepared, writers);
        var plan = prepared.Plan;
        var backup = Path.Combine(prepared.WorkPath, "before");
        if (Matches(backup, plan.Before.Snapshot, plan.Before.OriginalObjects) && IsMissing(plan.TargetPath)) return;
        VerifyTree(plan.TargetPath, plan.Before.Snapshot, plan.Before.OriginalObjects);
        RequireMissing(backup);
        if (plan.Before.Snapshot.Shape == FileObjectKind.Missing) return;
        Move(plan.TargetPath, backup, plan.Before.Snapshot.Shape);
        VerifyTree(backup, plan.Before.Snapshot, plan.Before.OriginalObjects);
    }

    public void InstallCandidate(PreparedConfigurationExchange prepared, ConfigurationWriterObservation writers)
    {
        using var pathLease = StableDirectoryLease.Acquire(prepared.WorkPath);
        VerifyPrepared(prepared, writers);
        var plan = prepared.Plan;
        var candidate = Path.Combine(prepared.WorkPath, "candidate");
        VerifyTree(Path.Combine(prepared.WorkPath, "before"), plan.Before.Snapshot, plan.Before.OriginalObjects);
        if (Matches(plan.TargetPath, plan.Candidate.Snapshot, prepared.CandidateObjects) && IsMissing(candidate)) return;
        RequireMissing(plan.TargetPath);
        VerifyTree(candidate, plan.Candidate.Snapshot, prepared.CandidateObjects);
        if (plan.Candidate.Snapshot.Shape == FileObjectKind.Missing) return;
        Move(candidate, plan.TargetPath, plan.Candidate.Snapshot.Shape);
        VerifyTree(plan.TargetPath, plan.Candidate.Snapshot, prepared.CandidateObjects);
    }

    public void RetireCandidate(PreparedConfigurationExchange prepared, ConfigurationWriterObservation writers)
    {
        using var pathLease = StableDirectoryLease.Acquire(prepared.WorkPath);
        VerifyPrepared(prepared, writers);
        var plan = prepared.Plan;
        var spent = Path.Combine(prepared.WorkPath, "spent");
        if (Matches(plan.TargetPath, plan.Before.Snapshot, plan.Before.OriginalObjects) && IsMissing(Path.Combine(prepared.WorkPath, "before"))) return;
        if (IsMissing(plan.TargetPath) && (Matches(spent, plan.Candidate.Snapshot, prepared.CandidateObjects) ||
            Matches(Path.Combine(prepared.WorkPath, "candidate"), plan.Candidate.Snapshot, prepared.CandidateObjects))) return;
        VerifyTree(plan.TargetPath, plan.Candidate.Snapshot, prepared.CandidateObjects);
        RequireMissing(spent);
        if (plan.Candidate.Snapshot.Shape == FileObjectKind.Missing) return;
        Move(plan.TargetPath, spent, plan.Candidate.Snapshot.Shape);
        VerifyTree(spent, plan.Candidate.Snapshot, prepared.CandidateObjects);
    }

    public void RestoreOriginal(PreparedConfigurationExchange prepared, ConfigurationWriterObservation writers)
    {
        using var pathLease = StableDirectoryLease.Acquire(prepared.WorkPath);
        VerifyPrepared(prepared, writers);
        var plan = prepared.Plan;
        var backup = Path.Combine(prepared.WorkPath, "before");
        if (Matches(plan.TargetPath, plan.Before.Snapshot, plan.Before.OriginalObjects) && IsMissing(backup)) return;
        RequireMissing(plan.TargetPath);
        VerifyTree(backup, plan.Before.Snapshot, plan.Before.OriginalObjects);
        if (plan.Before.Snapshot.Shape == FileObjectKind.Missing) return;
        Move(backup, plan.TargetPath, plan.Before.Snapshot.Shape);
        VerifyTree(plan.TargetPath, plan.Before.Snapshot, plan.Before.OriginalObjects);
    }

    public void VerifyRestored(PreparedConfigurationExchange prepared, ConfigurationWriterObservation writers)
    {
        using var pathLease = StableDirectoryLease.Acquire(prepared.WorkPath);
        VerifyPrepared(prepared, writers);
        VerifyTree(prepared.Plan.TargetPath, prepared.Plan.Before.Snapshot, prepared.Plan.Before.OriginalObjects);
        RequireMissing(Path.Combine(prepared.WorkPath, "before"));
    }

    internal void VerifyEditableScene(PreparedConfigurationExchange prepared, ConfigurationWriterObservation writers)
    {
        VerifyWriters(prepared.Plan, writers);
        using var held = HoldWritableWorkspace(prepared);
    }

    internal IDisposable HoldWritableWorkspace(PreparedConfigurationExchange prepared)
    {
        var path = StableDirectoryLease.Acquire(prepared.WorkPath);
        try
        {
            VerifyWorkspace(prepared);
            var before = TreeReadLease.Acquire(Path.Combine(prepared.WorkPath, "before"), prepared.WorkIdentity, prepared.Plan.Before);
            try
            {
                RequireMissing(Path.Combine(prepared.WorkPath, "candidate")); RequireMissing(Path.Combine(prepared.WorkPath, "spent"));
                return new WritableWorkspaceLease(path, before);
            }
            catch { before.Dispose(); throw; }
        }
        catch { path.Dispose(); throw; }
    }

    public bool CanRestore(PreparedConfigurationExchange prepared, ConfigurationWriterObservation writers)
    {
        using var pathLease = StableDirectoryLease.Acquire(prepared.WorkPath);
        VerifyPrepared(prepared, writers);
        var plan = prepared.Plan;
        var backup = Path.Combine(prepared.WorkPath, "before");
        var candidate = Path.Combine(prepared.WorkPath, "candidate");
        var spent = Path.Combine(prepared.WorkPath, "spent");
        var targetBefore = Matches(plan.TargetPath, plan.Before.Snapshot, plan.Before.OriginalObjects);
        var targetCandidate = Matches(plan.TargetPath, plan.Candidate.Snapshot, prepared.CandidateObjects);
        var targetMissing = IsMissing(plan.TargetPath);
        if (!targetBefore && !targetCandidate && !targetMissing) return false;
        var backupBefore = Matches(backup, plan.Before.Snapshot, plan.Before.OriginalObjects);
        if (!backupBefore && !IsMissing(backup)) return false;
        var candidateReady = Matches(candidate, plan.Candidate.Snapshot, prepared.CandidateObjects);
        var candidateSpent = Matches(spent, plan.Candidate.Snapshot, prepared.CandidateObjects);
        if (!candidateReady && !IsMissing(candidate) || !candidateSpent && !IsMissing(spent)) return false;
        if (plan.Before.Snapshot.Shape != FileObjectKind.Missing && (targetBefore ? 1 : 0) + (backupBefore ? 1 : 0) != 1) return false;
        if (plan.Candidate.Snapshot.Shape != FileObjectKind.Missing &&
            (targetCandidate ? 1 : 0) + (candidateReady ? 1 : 0) + (candidateSpent ? 1 : 0) != 1) return false;
        return true;
    }

    private void VerifyPrepared(PreparedConfigurationExchange prepared, ConfigurationWriterObservation writers)
    {
        VerifyWriters(prepared.Plan, writers);
        VerifyWorkspace(prepared);
    }

    private static void VerifyWorkspace(PreparedConfigurationExchange prepared)
    {
        VerifyParent(prepared.Plan);
        if (prepared.WorkPath != WorkPath(prepared.Plan) || PhysicalObjectIdentity.Read(prepared.WorkPath) != prepared.WorkIdentity ||
            prepared.WorkIdentity.VolumeSerial != prepared.Plan.ParentIdentity.VolumeSerial)
            throw new IOException("configuration_work_identity_changed");
        var known = new HashSet<string>(["candidate", "before", "spent"], StringComparer.Ordinal);
        if (Directory.EnumerateFileSystemEntries(prepared.WorkPath).Any(path => !known.Contains(Path.GetFileName(path))))
            throw new IOException("configuration_work_unknown_entry");
    }

    private static void VerifyWriters(FrozenConfigurationExchange plan, ConfigurationWriterObservation writers)
    {
        if (writers.InstanceId != plan.InstanceId || writers.JournalId != plan.JournalId || !writers.IsComplete || writers.State != WriterState.Stopped)
            throw new IOException("configuration_writer_stop_unconfirmed");
    }

    private static void VerifyParent(FrozenConfigurationExchange plan)
    {
        var target = PhysicalPathPolicy.NormalizeWithoutLinks(plan.TargetPath);
        if (PhysicalObjectIdentity.Read(Path.GetDirectoryName(target)!) != plan.ParentIdentity)
            throw new IOException("configuration_parent_identity_changed");
    }

    private void VerifyTree(string path, FileTreeSnapshot expected, IReadOnlyList<PhysicalTreeObject> identities)
    {
        if (!Matches(path, expected, identities)) throw new IOException("configuration_tree_or_identity_changed");
    }

    private bool Matches(string path, FileTreeSnapshot expected, IReadOnlyList<PhysicalTreeObject> identities)
    {
        var actual = scanner.Capture(path, CancellationToken.None);
        return actual.Shape == expected.Shape && actual.Entries.SequenceEqual(expected.Entries) &&
            PhysicalTreeIdentity.Capture(path, actual).SequenceEqual(identities);
    }

    private bool IsMissing(string path) => scanner.Capture(path, CancellationToken.None).Shape == FileObjectKind.Missing;
    private void RequireMissing(string path)
    {
        if (!IsMissing(path)) throw new IOException("configuration_destination_not_empty");
    }

    private static string WorkPath(FrozenConfigurationExchange plan) => PhysicalPathPolicy.NormalizeWithoutLinks(
        Path.Combine(Path.GetDirectoryName(plan.TargetPath)!, ".nexus-" + plan.JournalId.ToString("D")));

    private static void Move(string source, string destination, FileObjectKind shape)
    {
        PhysicalPathPolicy.NormalizeWithoutLinks(source);
        PhysicalPathPolicy.NormalizeWithoutLinks(destination);
        if (PhysicalObjectIdentity.Read(Path.GetDirectoryName(source)!).VolumeSerial != PhysicalObjectIdentity.Read(Path.GetDirectoryName(destination)!).VolumeSerial)
            throw new IOException("configuration_cross_volume_move_rejected");
        if (shape == FileObjectKind.File) File.Move(source, destination, overwrite: false);
        else if (shape == FileObjectKind.Directory) Directory.Move(source, destination);
        else throw new IOException("configuration_move_shape_invalid");
    }

    private sealed class WritableWorkspaceLease(IDisposable path, IDisposable before) : IDisposable
    {
        public void Dispose() { before.Dispose(); path.Dispose(); }
    }
}
