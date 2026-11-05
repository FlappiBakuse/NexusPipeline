using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record InstalledActivationResult(string ExtensionId, long InstalledRevision, Guid ActivationId);

[SupportedOSPlatform("windows")]
internal sealed class InstalledActivationService(RuntimeInstance instance, StateDatabase database,
    InstanceLease lease, ContractReader contracts, PayloadApplyRoots roots, ResourceLeaseManager resources,
    RecoveryIndex recovery, ExtensionCatalogStore catalog, ExtensionActivator activator)
{
    internal async ValueTask<InstalledActivationResult> ActivateAsync(string extensionId,
        CancellationToken acceptedHostCancellation)
    {
        lease.VerifyHeld(instance);
        lease.RequireExtensionActivator(activator, resources);
        acceptedHostCancellation.ThrowIfCancellationRequested();
        if (!resources.UsesRecoveryIndex(recovery)) throw new IOException("extension_installed_activation_unbound");
        ExtensionProjectionPublication.PublishIfDirty(instance, database, lease, contracts, roots, resources, recovery);
        IReadOnlyList<ResourceClaim> claims;
        using (DirectoryAttributeScope.Hold(instance))
        {
            var installed = new ExtensionInstalledEvidence(instance, database, contracts, roots).ReadCurrent(extensionId)
                ?? throw new IOException("extension_installed_activation_unbound");
            claims = InstalledActivationOrigin.Claims(instance, installed);
        }
        using var root = resources.TryAcquire(Guid.NewGuid(), claims).Lease
            ?? throw new IOException("extension_installed_activation_resources_unavailable");
        using var inventory = await activator.ReserveForApplyAsync(root, extensionId, acceptedHostCancellation).ConfigureAwait(false);
        return await ActivateReservedAsync(root, inventory, extensionId, acceptedHostCancellation).ConfigureAwait(false);
    }

    internal async ValueTask<InstalledActivationResult> ActivateReservedAsync(ResourceOperationLease root,
        ExtensionActivator.ApplyReservation inventory, string extensionId, CancellationToken acceptedHostCancellation)
    {
        lease.VerifyHeld(instance); lease.RequireExtensionActivator(activator, resources);
        acceptedHostCancellation.ThrowIfCancellationRequested();
        using var origin = InstalledActivationOrigin.Prepare(instance, database, lease, contracts, roots, catalog,
            resources, recovery, root, inventory, extensionId);
        await using var source = PreparedExtensionActivation.PrepareInstalled(origin, instance, database, lease,
            contracts, roots, root, inventory, acceptedHostCancellation);
        await using var activation = await inventory.PrepareCandidateAsync(source).ConfigureAwait(false);
        activation.PublishInstalled();
        _ = activation.CompleteCommitted();
        return new(extensionId, origin.InstalledRevision, activation.ActivationId);
    }

    internal async ValueTask<IReadOnlyList<InstalledActivationResult>> ActivateEnabledAsync(
        CancellationToken acceptedHostCancellation)
    {
        lease.VerifyHeld(instance);
        lease.RequireExtensionActivator(activator, resources);
        acceptedHostCancellation.ThrowIfCancellationRequested();
        string[] ids;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            ids = HostSettingsRows.ReadCurrent(connection, transaction).Values.Extensions.Where(item => item.Enabled)
                .Where(item => ExtensionInstalledRows.ReadCurrentMetadata(connection, transaction, item.ExtensionId)
                    is { IsInstalled: true }).Select(item => item.ExtensionId).ToArray();
            transaction.Commit();
        }
        var result = new List<InstalledActivationResult>(ids.Length);
        foreach (var id in ids)
        {
            acceptedHostCancellation.ThrowIfCancellationRequested();
            result.Add(await ActivateAsync(id, acceptedHostCancellation).ConfigureAwait(false));
        }
        return result.AsReadOnly();
    }
}
