using System.Runtime.Versioning;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Installation;

[SupportedOSPlatform("windows")]
internal sealed class InstallerRetainedRuntime(InstallerPaths paths)
{
    internal InstallationIdentity Read(InstallerCheckpoint frame)
    {
        var before = InstallerSnapshotCodec.Decode(frame.Before, frame.Root);
        if (!before.Ownership.Exists || before.Uninstall.Exists || before.Files.Length != 2 ||
            before.Files.Any(file => file.Role != "manager"))
            throw InstallationIdentityCodec.Invalid();
        var identity = new InstallerIdentityPublication(paths).Read(frame, original: true);
        var worker = before.Files.SingleOrDefault(file => file.Name == InstallationAnchor.WorkerFile);
        if (identity.State != "retained-data" || worker is null ||
            identity.Worker != new InstallerFile(worker.SizeBytes, worker.Sha256, worker.Identity))
            throw InstallationIdentityCodec.Invalid();
        return identity;
    }

    internal void RequireDestination(InstallationDestination destination, bool checkpoint = false)
    {
        if (destination is not { Kind: InstallationDestinationKind.Retained, Identity: { } identity })
            throw InstallationIdentityCodec.Invalid();
        var metadata = InstallerMetadataInventory.Capture(destination.Root, paths);
        if (metadata.Uninstall.Exists || metadata.Files.Length != 2 || metadata.Files.Any(file => file.Role != "manager"))
            throw InstallationIdentityCodec.Invalid();
        var bundled = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(destination.Root, "plugins"));
        if (File.Exists(bundled) || Directory.Exists(bundled) && Directory.EnumerateFileSystemEntries(bundled).Any())
            throw InstallationIdentityCodec.Invalid();
        RequireManager(checkpoint);
        RequireRuntime(identity);
    }

    internal RuntimeInstance RequireRuntime(InstallationIdentity identity)
    {
        var runtime = new InstanceRoot(new ContractReader()).OpenExisting(Path.Combine(identity.ApplicationRoot, "runtime"));
        if (runtime.InstanceId != identity.RuntimeId || PhysicalObjectIdentity.Read(runtime.RuntimeRoot) != identity.RuntimeIdentity ||
            PhysicalObjectIdentity.Read(identity.ApplicationRoot) != identity.RootIdentity) throw InstallationIdentityCodec.Invalid();
        return runtime;
    }

    internal void Admit(InstallationIdentity identity, ProcessIdentity setup, string? workRoot)
    {
        if (string.IsNullOrWhiteSpace(workRoot)) throw new IOException("installer_runtime_inspection_required");
        InstallerRuntimeGate.RequireHeld(identity.ApplicationRoot);
        foreach (var name in new[] { ApplicationTree.HostEntry, ApplicationTree.MaintenanceEntry, ApplicationTree.DesktopEntry })
            ExclusiveProgramAdmission.RequireStopped(Path.Combine(identity.ApplicationRoot, name));
        var runtime = RequireRuntime(identity);
        using var lease = InstanceLease.AcquireInstaller(runtime, new ContractReader(), setup);
        _ = new InstallerRuntimeAdmission(runtime, new(), new(identity.ApplicationRoot, Path.Combine(runtime.RuntimeRoot, "extensions")))
            .Open(lease, workRoot);
    }

    internal void RequireManager(bool checkpoint)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(paths.ManagerRoot))
            if (Path.GetFileName(path) is not (InstallationAnchor.IdentityFile or InstallationAnchor.WorkerFile) &&
                !(checkpoint && Path.GetFileName(path) == "metadata-checkpoint" && Directory.Exists(path)))
                throw InstallationIdentityCodec.Invalid();
    }
}
