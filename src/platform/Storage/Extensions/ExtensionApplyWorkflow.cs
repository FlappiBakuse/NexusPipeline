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
        Action<PayloadAcceptanceResult> accepted, CancellationToken hostCancellation, LocalExtensionInput? input = null)
    {
        _ = request.Identity(operationId, subject);
        if (ReadAccepted(operationId, subject, request) is { } replay)
        { accepted(replay); return new(replay, null, true); }
        lease.VerifyHeld(instance); lease.RequireExtensionActivator(activator, resources);
        Publish(); hostCancellation.ThrowIfCancellationRequested();
        if (request.Action == PayloadAction.Remove)
            return await ApplyAsync(operationId, subject, request, null, accepted, hostCancellation).ConfigureAwait(false);
        if ((request.LocalApproval is not null || request.LocalUpdateConfirmation is not null) && input is null) throw Invalid();
        if (request.LocalApproval is null && (catalogs is null || transport is null))
            throw new IOException("extension_release_trust_unprovisioned");
        using var packageRoot = await AcquireAsync(operationId, () => request.Catalog is null
            ? [new("blobs:content", ResourceClaimMode.Reference)]
            : [new("extensions:catalog", ResourceClaimMode.Reference), new("blobs:content", ResourceClaimMode.Reference)],
            hostCancellation).ConfigureAwait(false);
        var entry = request.Candidate!;
        var catalog = request.Catalog is null ? null : catalogs!.ReadCached(request.Catalog.Channel) ?? throw Invalid();
        if (catalog is not null && (catalog.Identity != request.Catalog ||
            catalog.Entries.SingleOrDefault(item => item.ExtensionId == request.ExtensionId) != entry)) throw Invalid();
        using var draft = input?.PrepareArchive(entry, hostCancellation);
        var blobs = new ImmutableBlobStore(instance, contracts);
        var archivePath = Path.Combine(instance.RuntimeRoot, $"blobs/content/{entry.ArtifactSha256}.bin");
        if (draft is not null)
        {
            var content = blobs.PersistContent(draft.Path, new(Path.GetFileName(draft.Path),
                FileObjectKind.File, entry.ArtifactBytes, entry.ArtifactSha256), hostCancellation);
            archivePath = Path.Combine(instance.RuntimeRoot, content.RelativePath);
        }
        using var cached = File.Exists(archivePath) ? blobs.OpenVerified(new($"blobs/content/{entry.ArtifactSha256}.bin",
            entry.ArtifactBytes, entry.ArtifactSha256)) : null;
        using var downloaded = cached is null ? await OwnedExtensionDownload.Receive(instance, lease, resources, operationId,
            entry, (destination, cancellation) => transport!.Artifact(catalog ?? throw Invalid(), request.ExtensionId,
                destination, cancellation), hostCancellation).ConfigureAwait(false) : null;
        if (downloaded is not null) archivePath = downloaded.Adopt(blobs, hostCancellation);
        using var verified = catalog is { } official
            ? VerifiedExtensionPackage.Prepare(catalogs!, official.Identity.Channel, operationId,
                packageRoot, request.ExtensionId, archivePath, contracts, hostCancellation)
            : VerifiedExtensionPackage.PrepareLocal(instance, resources, packageRoot, entry,
                request.LocalApproval!, archivePath, contracts, hostCancellation);
        var candidate = verified.CaptureCandidate(packageRoot, blobs, hostCancellation);
        ArchivedFileTree? import = null;
        if (input?.Tree is { } tree)
        {
            candidate.Use(packageRoot, blobs, hostCancellation, (_, archive) =>
            {
                if (!archive.Snapshot.Entries.SequenceEqual(tree.Snapshot.Entries))
                    throw new IOException("extension_local_official_verification_required");
                return true;
            });
            if (request.ImportTarget)
            {
                var captured = new FileTreeArchive(new(new(4096, 268435456)), blobs).Capture(input.Path, hostCancellation);
                if (!captured.OriginalObjects.SequenceEqual(tree.OriginalObjects) ||
                    !captured.Snapshot.Entries.SequenceEqual(tree.Snapshot.Entries)) throw Invalid();
                import = captured;
            }
        }
        return await ApplyAsync(operationId, subject, request, candidate, accepted, hostCancellation, import).ConfigureAwait(false);
    }

    private async Task<ExtensionApplyCompletion> ApplyAsync(Guid operationId, string subject, ExtensionApplyRequest request,
        VerifiedExtensionPackage.Candidate? candidate, Action<PayloadAcceptanceResult> accepted, CancellationToken cancellation,
        ArchivedFileTree? importTarget = null)
    {
        using var root = await AcquireAsync(operationId, () => Claims(request, operationId), cancellation).ConfigureAwait(false);
        return await ApplyReservedAsync(root, subject, request, candidate, accepted, cancellation, importTarget: importTarget).ConfigureAwait(false);
    }

    internal async Task<bool> RemoveMissingAsync(ExtensionApplyRequest request, CancellationToken cancellation)
    {
        lease.VerifyHeld(instance); Publish(); cancellation.ThrowIfCancellationRequested();
        var operation = Guid.NewGuid();
        using var root = resources.TryAcquire(operation, Claims(request, operation)).Lease;
        if (root is null) return false;
        _ = await ApplyReservedAsync(root, "host:external-removal", request, null, _ => { }, cancellation, true).ConfigureAwait(false);
        return true;
    }

    private async Task<ExtensionApplyCompletion> ApplyReservedAsync(ResourceOperationLease root, string subject,
        ExtensionApplyRequest request, VerifiedExtensionPackage.Candidate? candidate,
        Action<PayloadAcceptanceResult> accepted, CancellationToken cancellation, bool externalRemoval = false, ArchivedFileTree? importTarget = null)
    {
        using var prepared = PreparedExtensionPayload.Prepare(instance, database, lease, resources, recovery, root,
            request.Action, request.ExtensionId, request.ExpectedInstalledRevision, request.ExpectedSettingsRevision,
            candidate, roots, contracts, subject, cancellation, externalRemoval, importTarget, request.LocalUpdateConfirmation);
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
        if (request.Catalog is not null) logical.Add(new("extensions:catalog", ResourceClaimMode.Reference));
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
            var selection = claims();
            // Recovery proof temporarily suspends admission while its publication is in progress.
            var admission = lease.PublishExecution(() => resources.TryAcquire(operationId, selection));
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
