using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record ExtensionApplyCompletion(PayloadAcceptanceResult Acceptance,
    PayloadApplyResult? Result, bool WasReplay);

[SupportedOSPlatform("windows")]
internal sealed class ExtensionApplyWorkflow(RuntimeInstance instance, StateDatabase database,
    InstanceLease lease, ContractReader contracts, PayloadApplyRoots roots, ResourceLeaseManager resources,
    RecoveryIndex recovery, ExtensionCatalogStore? catalogs, ExtensionActivator activator,
    StoreHttpTransport? transport, IPayloadPersistenceObserver? observer = null)
{
    internal PayloadAcceptanceResult? ReadAccepted(Guid operationId, string subject, ExtensionApplyRequest request) =>
        lease.PublishExecution(() => new PayloadAcceptanceLookup(instance, database, contracts, roots)
            .ReadAccepted(operationId, subject, request));

    internal async Task<ExtensionApplyCompletion> RunAsync(Guid operationId, string subject, ExtensionApplyRequest request,
        Action<PayloadAcceptanceResult> accepted, CancellationToken hostCancellation)
    {
        _ = request.Identity(operationId, subject);
        if (ReadAccepted(operationId, subject, request) is { } replay)
        { accepted(replay); return new(replay, null, true); }
        lease.VerifyHeld(instance); lease.RequireExtensionActivator(activator, resources);
        Publish(); hostCancellation.ThrowIfCancellationRequested();
        if (request.Action == PayloadAction.Remove)
            return await ApplyAsync(operationId, subject, request, null, accepted, hostCancellation).ConfigureAwait(false);
        if (catalogs is null || transport is null) throw new IOException("extension_release_trust_unprovisioned");
        using var packageRoot = await AcquireAsync(operationId, () =>
            [new("extensions:catalog", ResourceClaimMode.Reference), new("blobs:content", ResourceClaimMode.Reference)],
            hostCancellation).ConfigureAwait(false);
        var catalog = catalogs.ReadCached(request.Catalog!.Channel) ?? throw Invalid();
        if (catalog.Identity != request.Catalog ||
            catalog.Entries.SingleOrDefault(item => item.ExtensionId == request.ExtensionId) != request.Candidate) throw Invalid();
        using var downloaded = await OwnedExtensionDownload.Receive(instance, lease, resources, operationId,
            request.Candidate!, (destination, cancellation) => transport.Artifact(catalog, request.ExtensionId,
                destination, cancellation), hostCancellation).ConfigureAwait(false);
        using var verified = VerifiedExtensionPackage.Prepare(catalogs, catalog.Identity.Channel, operationId,
            packageRoot, request.ExtensionId, downloaded.ArchivePath, contracts, hostCancellation);
        var candidate = verified.CaptureCandidate(packageRoot, new(instance, contracts), hostCancellation);
        return await ApplyAsync(operationId, subject, request, candidate, accepted, hostCancellation).ConfigureAwait(false);
    }

    private async Task<ExtensionApplyCompletion> ApplyAsync(Guid operationId, string subject, ExtensionApplyRequest request,
        VerifiedExtensionPackage.Candidate? candidate, Action<PayloadAcceptanceResult> accepted, CancellationToken cancellation)
    {
        using var root = await AcquireAsync(operationId, () => Claims(request, operationId), cancellation).ConfigureAwait(false);
        using var prepared = PreparedExtensionPayload.Prepare(instance, database, lease, resources, recovery, root,
            request.Action, request.ExtensionId, request.ExpectedInstalledRevision, request.ExpectedSettingsRevision,
            candidate, roots, contracts, subject, cancellation);
        cancellation.ThrowIfCancellationRequested();
        var acceptance = new PayloadApplyAcceptance(instance, contracts, database, roots, observer)
            .AcceptPrepared(prepared, DateTimeOffset.UtcNow);
        accepted(acceptance);
        if (!acceptance.Acceptance.IsNew) return new(acceptance, null, true);
        var result = await new PayloadApplySession(instance, database, lease, contracts, roots, activator, observer)
            .ApplyAsync(prepared, cancellation).ConfigureAwait(false);
        Publish();
        return new(acceptance, result, false);
    }

    private IReadOnlyList<ResourceClaim> Claims(ExtensionApplyRequest request, Guid operationId)
    {
        var target = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(roots.ExtensionPayloadRoot, request.ExtensionId));
        var settings = new HostSettingsStore(database).ReadExtensionIntent(request.ExtensionId, request.ExpectedInstalledRevision == 0);
        if (settings.SettingsRevision != request.ExpectedSettingsRevision) throw Invalid();
        var logical = new List<ResourceClaim>
        {
            new("extension:" + request.ExtensionId, ResourceClaimMode.Mutation),
            new("blobs:content", ResourceClaimMode.Reference),
            new("settings:host", settings.InitializeEnabledPreference ? ResourceClaimMode.Mutation : ResourceClaimMode.Reference)
        };
        if (request.Candidate is not null) logical.Add(new("extensions:catalog", ResourceClaimMode.Reference));
        return PhysicalResourceAdmission.Merge(logical.Concat(PhysicalResourceAdmission.Read(target))
            .Concat(PhysicalResourceAdmission.Read(ExtensionWorkspacePath.For(target, operationId))));
    }

    private async Task<ResourceOperationLease> AcquireAsync(Guid operationId,
        Func<IReadOnlyList<ResourceClaim>> claims, CancellationToken cancellation)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested(); lease.VerifyHeld(instance);
            var admission = resources.TryAcquire(operationId, claims());
            if (admission.Lease is { } root) return root;
            if (admission.Denial != ResourceLeaseDenial.Conflict) throw new IOException("extension_apply_host_unavailable");
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMinutes(15)) throw new IOException("extension_apply_resource_wait_expired");
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellation).ConfigureAwait(false);
        }
    }

    private void Publish() => ExtensionProjectionPublication.PublishIfDirty(instance, database, lease,
        contracts, roots, resources, recovery);
    private static IOException Invalid() => new("extension_apply_selection_changed");
}
