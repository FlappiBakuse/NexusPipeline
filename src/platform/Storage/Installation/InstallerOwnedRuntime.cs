using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Installation;

internal sealed record InstallerOwnedSource(string Format, int Version, string Profile, ProcessIdentity SetupOwner,
    InstallationIdentity Identity, InstallerMetadataSnapshot Metadata, FileTreeSnapshot Application,
    IReadOnlyList<PhysicalTreeObject> ApplicationObjects);

[SupportedOSPlatform("windows")]
internal sealed class InstallerOwnedRuntime(InstallerPaths paths, string profile)
{
    internal TResult Use<TResult>(string root, ProcessIdentity setup, string workRoot, Func<InstallerOwnedSource, TResult> read,
        bool checkpoint = false)
    {
        InstallerCheckpointStore.RequireCaller(setup);
        InstallerRuntimeGate.RequireHeld(root);
        var destination = InstallationDestination.Inspect(root, paths, InstallerPaths.ProtectedRoots());
        if (destination is not { Kind: InstallationDestinationKind.Owned, Identity: { State: "active" } identity })
            throw new IOException("installer_active_installation_required");
        RequireInspectionRoot(destination.Root, workRoot);
        new InstallerRetainedRuntime(paths).RequireManager(checkpoint);
        var metadata = InstallerMetadataInventory.Capture(destination.Root, paths);
        InstallerRegistration.RequireNative(metadata, identity.ProductVersion);
        foreach (var name in new[] { ApplicationTree.HostEntry, ApplicationTree.LauncherEntry, ApplicationTree.MaintenanceEntry, ApplicationTree.DesktopEntry })
            ExclusiveProgramAdmission.RequireStopped(Path.Combine(destination.Root, name));
        using var payload = InstallationPayload.Open(destination.Root, identity.ManifestSha256, profile);
        if (payload.RootIdentity != identity.RootIdentity) throw InstallationIdentityCodec.Invalid();
        var runtime = new InstallerRetainedRuntime(paths).RequireRuntime(identity);
        using var lease = InstanceLease.AcquireInstaller(runtime, new ContractReader(), setup);
        _ = new InstallerRuntimeAdmission(runtime, new(), new(destination.Root, Path.Combine(runtime.RuntimeRoot, "extensions")))
            .Open(lease, workRoot);
        if (!InstallerMetadataInventory.Equal(metadata, InstallerMetadataInventory.CaptureFrozen(metadata, paths)))
            throw InstallationIdentityCodec.Invalid();
        return read(new("nexus.installer.owned-source", 1, profile, setup, identity, metadata, payload.Snapshot, payload.Objects));
    }

    internal void RequireInspectionRoot(string application, string requested)
    {
        var work = Path.TrimEndingDirectorySeparator(PhysicalPathPolicy.NormalizeWithoutLinks(requested));
        foreach (var reserved in new[] { application, paths.ManagerRoot })
        {
            var other = Path.TrimEndingDirectorySeparator(reserved);
            if (StringComparer.OrdinalIgnoreCase.Equals(work, other) ||
                work.StartsWith(other + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                other.StartsWith(work + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("installer_inspection_root_overlaps_installation");
        }
    }
}
