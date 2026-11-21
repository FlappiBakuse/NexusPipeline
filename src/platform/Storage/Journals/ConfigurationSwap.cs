using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;

namespace NexusPipeline.Platform.Storage.Journals;

public sealed record ConfigurationWriterObservation(Guid InstanceId, Guid JournalId, bool IsComplete, WriterState State);
public sealed record PreparedConfigurationExchange(FrozenConfigurationExchange Plan, string WorkPath,
    PhysicalObjectIdentity WorkIdentity, IReadOnlyList<PhysicalTreeObject> CandidateObjects)
{
    internal bool ReconstructionStarted { get; init; }
    internal ConfigurationRestorationProof? Reconstruction { get; init; }
}

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
        if (MatchesRestored(prepared) && IsMissing(Path.Combine(prepared.WorkPath, "before"))) return;
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
        if (MatchesRestored(prepared) && IsMissing(backup) && IsMissing(ReconstructedPath(prepared))) return;
        RequireMissing(plan.TargetPath);
        if (prepared.Reconstruction is not null)
        {
            RequireMissing(backup);
            var staged = ReconstructedPath(prepared);
            var objects = ReadReconstructed(staged, prepared);
            PhysicalObjectMove.MoveVerified(new(staged, prepared.WorkIdentity, objects[0].Identity,
                plan.TargetPath, plan.ParentIdentity, plan.Before.Snapshot.Shape));
            _ = ReadReconstructed(plan.TargetPath, prepared);
            return;
        }
        VerifyTree(backup, plan.Before.Snapshot, plan.Before.OriginalObjects);
        if (plan.Before.Snapshot.Shape == FileObjectKind.Missing) return;
        Move(backup, plan.TargetPath, plan.Before.Snapshot.Shape);
        VerifyTree(plan.TargetPath, plan.Before.Snapshot, plan.Before.OriginalObjects);
    }

    public void VerifyRestored(PreparedConfigurationExchange prepared, ConfigurationWriterObservation writers)
    {
        using var held = HoldRestored(prepared, writers);
    }

    internal TreeReadLease HoldRestored(PreparedConfigurationExchange prepared, ConfigurationWriterObservation writers, bool workspaceCleaned = false)
    {
        using var pathLease = workspaceCleaned ? null : StableDirectoryLease.Acquire(prepared.WorkPath);
        var plan = prepared.Plan;
        VerifyWriters(plan, writers);
        if (!workspaceCleaned) VerifyWorkspace(prepared);
        var before = prepared.Reconstruction is null ? plan.Before :
            plan.Before with { OriginalObjects = ReadReconstructed(plan.TargetPath, prepared) };
        var target = TreeReadLease.Acquire(plan.TargetPath, plan.ParentIdentity, before);
        try
        {
            if (workspaceCleaned) RequireMissing(prepared.WorkPath);
            else
            {
                RequireMissing(Path.Combine(prepared.WorkPath, "before"));
                RequireMissing(ReconstructedPath(prepared));
            }
            return target;
        }
        catch { target.Dispose(); throw; }
    }

    internal void VerifyEditableScene(PreparedConfigurationExchange prepared, ConfigurationWriterObservation writers)
    {
        VerifyWriters(prepared.Plan, writers);
        using var held = HoldWritableWorkspace(prepared, allowMissingOriginal: true);
    }

    internal IDisposable HoldWritableWorkspace(PreparedConfigurationExchange prepared, bool allowMissingOriginal = false)
    {
        var path = StableDirectoryLease.Acquire(prepared.WorkPath);
        try
        {
            VerifyWorkspace(prepared);
            var backup = Path.Combine(prepared.WorkPath, "before");
            var beforeTree = allowMissingOriginal && IsMissing(backup) ? new ArchivedFileTree(new(FileObjectKind.Missing, []), [], []) : prepared.Plan.Before;
            var before = TreeReadLease.Acquire(backup, prepared.WorkIdentity, beforeTree);
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
        => InspectRestoration(prepared, writers).FailureCode is null;

    internal (bool NeedsReconstruction, string? FailureCode) InspectRestoration(PreparedConfigurationExchange prepared, ConfigurationWriterObservation writers)
    {
        using var pathLease = StableDirectoryLease.Acquire(prepared.WorkPath);
        VerifyPrepared(prepared, writers, inspectInterruptedReconstruction: true);
        var scene = InspectScene(prepared);
        return scene.NeedsReconstruction && IsMissing(ReconstructedPath(prepared)) ?
            (true, archive.InspectRestorationSource(prepared.Plan.Before)) : scene;
    }

    internal ConfigurationRestorationProof StageOriginal(PreparedConfigurationExchange prepared, ConfigurationWriterObservation writers)
    {
        using var pathLease = StableDirectoryLease.Acquire(prepared.WorkPath);
        VerifyPrepared(prepared, writers, inspectInterruptedReconstruction: true);
        if (!prepared.ReconstructionStarted || !InspectScene(prepared).NeedsReconstruction)
            throw new IOException("configuration_reconstruction_scene_changed");
        var source = prepared.Plan.Before;
        var staged = ReconstructedPath(prepared);
        if (IsMissing(staged)) archive.MaterializeNew(source, staged, CancellationToken.None);
        var objects = ReadReconstructed(staged, prepared);
        return new(ConfigurationRestorationProof.Digest(objects));
    }

    private (bool NeedsReconstruction, string? FailureCode) InspectScene(PreparedConfigurationExchange prepared)
    {
        const string unverified = "configuration_state_unverified";
        var plan = prepared.Plan;
        var backup = Path.Combine(prepared.WorkPath, "before");
        var candidate = Path.Combine(prepared.WorkPath, "candidate");
        var spent = Path.Combine(prepared.WorkPath, "spent");
        using var held = TreeReadLease.Capture(plan.TargetPath, plan.ParentIdentity, scanner, CancellationToken.None,
            "configuration.scene.hash", out var target);
        var targetBefore = SameTree(target.Snapshot, plan.Before.Snapshot) && (prepared.Reconstruction is null
            ? target.Objects.SequenceEqual(plan.Before.OriginalObjects) : HasReconstructionIdentity(target.Objects, prepared));
        var targetCandidate = SameTree(target.Snapshot, plan.Candidate.Snapshot) && target.Objects.SequenceEqual(prepared.CandidateObjects);
        if (!targetBefore && !targetCandidate && target.Snapshot.Shape != FileObjectKind.Missing) return (false, unverified);
        var backupBefore = Matches(backup, plan.Before.Snapshot, plan.Before.OriginalObjects);
        if (!backupBefore && !IsMissing(backup)) return (false, unverified);
        var candidateReady = Matches(candidate, plan.Candidate.Snapshot, prepared.CandidateObjects);
        var candidateSpent = Matches(spent, plan.Candidate.Snapshot, prepared.CandidateObjects);
        if (!candidateReady && !IsMissing(candidate) || !candidateSpent && !IsMissing(spent)) return (false, unverified);
        if (plan.Candidate.Snapshot.Shape != FileObjectKind.Missing &&
            (targetCandidate ? 1 : 0) + (candidateReady ? 1 : 0) + (candidateSpent ? 1 : 0) != 1) return (false, unverified);
        var staged = ReconstructedPath(prepared);
        if (prepared.Reconstruction is not null)
        {
            if (backupBefore || targetBefore != IsMissing(staged)) return (false, unverified);
            if (!targetBefore) _ = ReadReconstructed(staged, prepared);
            return (false, null);
        }
        if (plan.Before.Snapshot.Shape == FileObjectKind.Missing) return (false, null);
        if (!IsMissing(staged))
            return !targetBefore && !backupBefore && prepared.ReconstructionStarted && TryReadReconstructed(staged, prepared) is not null ?
                (true, null) : (false, unverified);
        return targetBefore && backupBefore ? (false, unverified) : (!targetBefore && !backupBefore, null);
    }

    private bool MatchesRestored(PreparedConfigurationExchange prepared)
    {
        var plan = prepared.Plan;
        return prepared.Reconstruction is null ? Matches(plan.TargetPath, plan.Before.Snapshot, plan.Before.OriginalObjects) :
            TryReadReconstructed(plan.TargetPath, prepared) is not null;
    }

    private IReadOnlyList<PhysicalTreeObject> ReadReconstructed(string path, PreparedConfigurationExchange prepared) =>
        TryReadReconstructed(path, prepared) ?? throw new IOException("configuration_reconstruction_unverified");

    private IReadOnlyList<PhysicalTreeObject>? TryReadReconstructed(string path, PreparedConfigurationExchange prepared)
    {
        var plan = prepared.Plan;
        var actual = scanner.Capture(path, CancellationToken.None);
        if (!SameTree(actual, plan.Before.Snapshot)) return null;
        PhysicalTreeIdentity.RequireSingleLinks(path, actual);
        var objects = PhysicalTreeIdentity.Capture(path, actual);
        return HasReconstructionIdentity(objects, prepared) ? objects : null;
    }

    private static bool HasReconstructionIdentity(IReadOnlyList<PhysicalTreeObject> objects, PreparedConfigurationExchange prepared)
    {
        var plan = prepared.Plan;
        var existing = plan.Before.OriginalObjects.Concat(prepared.CandidateObjects).Select(item => item.Identity).ToHashSet();
        existing.Add(prepared.WorkIdentity);
        existing.Add(plan.ParentIdentity);
        return objects.Select(item => item.Identity).Distinct().Count() == objects.Count &&
            objects.All(item => item.Identity.VolumeSerial == plan.ParentIdentity.VolumeSerial && !existing.Contains(item.Identity)) &&
            (prepared.Reconstruction is not { } proof || proof.ObjectDigest == ConfigurationRestorationProof.Digest(objects));
    }

    private static string ReconstructedPath(PreparedConfigurationExchange prepared) => Path.Combine(prepared.WorkPath, "reconstructed");

    private void VerifyPrepared(PreparedConfigurationExchange prepared, ConfigurationWriterObservation writers, bool inspectInterruptedReconstruction = false)
    {
        VerifyWriters(prepared.Plan, writers);
        VerifyWorkspace(prepared, inspectInterruptedReconstruction);
    }

    private static void VerifyWorkspace(PreparedConfigurationExchange prepared, bool inspectInterruptedReconstruction = false)
    {
        VerifyParent(prepared.Plan);
        if (prepared.WorkPath != WorkPath(prepared.Plan) || PhysicalObjectIdentity.Read(prepared.WorkPath) != prepared.WorkIdentity ||
            prepared.WorkIdentity.VolumeSerial != prepared.Plan.ParentIdentity.VolumeSerial)
            throw new IOException("configuration_work_identity_changed");
        var known = new HashSet<string>(["candidate", "before", "spent"], StringComparer.Ordinal);
        if (prepared.Reconstruction is not null || inspectInterruptedReconstruction && prepared.ReconstructionStarted) known.Add("reconstructed");
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
        return SameTree(actual, expected) && PhysicalTreeIdentity.Capture(path, actual).SequenceEqual(identities);
    }

    private static bool SameTree(FileTreeSnapshot actual, FileTreeSnapshot expected) =>
        actual.Shape == expected.Shape && actual.Entries.SequenceEqual(expected.Entries);

    private static bool IsMissing(string path)
    {
        var full = PhysicalPathPolicy.NormalizeWithoutLinks(path);
        try
        {
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new IOException("snapshot_link_rejected");
            return false;
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
    }

    private static void RequireMissing(string path)
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
