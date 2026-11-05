using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Installation;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Maintenance;

[SupportedOSPlatform("windows")]
internal sealed class MaintenanceInstallation(RuntimeInstance instance, ContractReader contracts)
{
    private readonly InstallationUpdateBinding _sources = new(instance, contracts);
    private readonly InstallationUpdateCheckpoint _checkpoints = new(instance, contracts);

    internal void RequireOriginal(HostUpdateSource source, FrozenPayloadApply plan, string profile)
    {
        if (source.Installation is null) { InstallationUpdateBinding.RequireUnowned(plan.TargetPath); return; }
        var original = _sources.Read(source.Installation, plan.TargetPath, plan.Before, profile);
        var paths = InstallationUpdateBinding.Paths(original);
        var pending = Directory.Exists(Path.Combine(paths.ManagerRoot, "metadata-checkpoint"));
        _sources.RequireCurrent(original, plan.TargetPath, pending);
        if (pending) InstallationUpdateCheckpoint.RequireFiles(_checkpoints.ReadPending(original, source, plan), paths, original: true);
    }

    internal BlobReference? PrepareCommit(HostUpdateSource source, FrozenPayloadApply plan, string profile)
    {
        RequireOriginal(source, plan, profile);
        return source.Installation is null ? null : _checkpoints.Prepare(
            _sources.Read(source.Installation, plan.TargetPath, plan.Before, profile), source, plan,
            ProcessIdentityReader.Read(Environment.ProcessId));
    }

    internal void RequireCommit(HostUpdateSource source, FrozenPayloadApply plan, string? profile, MaintenanceCommitEntry commit)
    {
        if (source.Installation is null)
        {
            if (commit.Intent.Installation is not null) throw InstallationUpdateBinding.Invalid();
            return;
        }
        if (commit.Intent.Installation is null) throw InstallationUpdateBinding.Invalid();
        var original = _sources.Read(source.Installation, plan.TargetPath, plan.Before, profile);
        var frame = _checkpoints.Read(commit.Intent.Installation, original, source, plan);
        if (frame.Checkpoint.Writer != commit.Host || frame.Checkpoint.SetupOwner != commit.Host) throw InstallationUpdateBinding.Invalid();
    }

    internal void Publish(HostUpdateSource source, FrozenPayloadApply plan, string profile, MaintenanceCommitEntry commit)
    {
        if (source.Installation is null) { InstallationUpdateBinding.RequireUnowned(plan.TargetPath); return; }
        RequireCommit(source, plan, profile, commit);
        var original = _sources.Read(source.Installation, plan.TargetPath, plan.Before, profile);
        var saved = _checkpoints.Read(commit.Intent.Installation!, original, source, plan);
        var frame = saved.Checkpoint;
        var paths = InstallationUpdateBinding.Paths(original);
        var after = InstallerSnapshotCodec.Decode(frame.After!, plan.TargetPath);
        var directory = Path.Combine(paths.ManagerRoot, "metadata-checkpoint");
        if (!Directory.Exists(directory))
        {
            InstallationUpdateBinding.RequireMembers(paths, checkpoint: false);
            if (!InstallerMetadataInventory.Equal(after, InstallerMetadataInventory.CaptureFrozen(after, paths))) throw InstallationUpdateBinding.Invalid();
            return;
        }
        InstallationUpdateBinding.RequireMembers(paths, checkpoint: true);
        InstallationUpdateCheckpoint.RequireFiles(saved, paths, original: false);
        if (!InstallerMetadataInventory.Equal(after, InstallerMetadataInventory.CaptureFrozen(after, paths)))
            new InstallerIdentityPublication(paths).Publish(frame);
        if (new InstallationAnchor(paths.ManagerRoot, paths.RegistryKey).Read(plan.TargetPath) != saved.Identity) throw InstallationUpdateBinding.Invalid();
        new InstallerCheckpointCompletion(paths, new(paths, profile)).CompleteFrozen(frame, committed: true);
    }

    internal void CancelPreparation(HostUpdateSource source, FrozenPayloadApply plan, string profile)
    {
        RequireOriginal(source, plan, profile);
        if (source.Installation is null) return;
        var original = _sources.Read(source.Installation, plan.TargetPath, plan.Before, profile);
        var paths = InstallationUpdateBinding.Paths(original);
        if (!Directory.Exists(Path.Combine(paths.ManagerRoot, "metadata-checkpoint"))) return;
        var frame = _checkpoints.ReadPending(original, source, plan);
        InstallationUpdateCheckpoint.RequireFiles(frame, paths, original: true);
        new InstallerCheckpointCompletion(paths, new(paths, profile)).CompleteFrozen(frame.Checkpoint, committed: false);
    }
}
