using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class ExtensionFileReconciliation(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
    ContractReader contracts, PayloadApplyRoots roots, ResourceLeaseManager resources, RecoveryIndex recovery,
    ExtensionActivator activator)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal async Task ReconcileAsync(CancellationToken cancellation)
    {
        if (!await _gate.WaitAsync(0, cancellation).ConfigureAwait(false)) return;
        try
        {
            lease.VerifyHeld(instance);
            foreach (var id in ExtensionInstalledRows.ReadIds(database, installedOnly: true))
            {
                cancellation.ThrowIfCancellationRequested();
                NexusPipeline.Core.Extensions.ExtensionIdentifiers.Require(id);
                try
                {
                    _ = File.GetAttributes(Path.Combine(roots.ExtensionPayloadRoot, id));
                    continue;
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
                var installed = new ExtensionInstalledEvidence(instance, database, contracts, roots).ReadCurrent(id)!;
                if (!installed.Metadata.IsInstalled || Observe(installed) != "extension_files_removed") continue;
                var request = new ExtensionApplyRequest(PayloadAction.Remove, id, installed.Metadata.Revision,
                    new HostSettingsStore(database).Read().Revision, null, null);
                var workflow = new ExtensionApplyWorkflow(instance, database, lease, contracts, roots, resources,
                    recovery, null, activator, null);
                _ = await workflow.RemoveMissingAsync(request, cancellation).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    internal static string? Observe(BoundExtensionInstallation installed)
    {
        if (!installed.Metadata.IsInstalled) return null;
        try
        {
            var target = PhysicalPathPolicy.NormalizeWithoutLinks(installed.Footprint.TargetPath);
            var parent = Path.GetDirectoryName(target)!;
            if (PhysicalObjectIdentity.Read(parent) != installed.Footprint.ParentIdentity)
                return "extension_directory_unavailable";
            try
            {
                var attributes = File.GetAttributes(target);
                return (attributes & FileAttributes.Directory) == 0 ? "extension_files_changed" : null;
            }
            catch (FileNotFoundException) { return "extension_files_removed"; }
            catch (DirectoryNotFoundException) { return "extension_directory_unavailable"; }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return "extension_directory_unavailable"; }
    }
}
