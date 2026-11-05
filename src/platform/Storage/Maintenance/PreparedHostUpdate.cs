using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Maintenance;

[SupportedOSPlatform("windows")]
internal sealed class PreparedHostUpdate : IDisposable
{
    internal static IReadOnlyList<ResourceClaim> PreparationClaims { get; } = Array.AsReadOnly<ResourceClaim>([
        new("blobs:content", ResourceClaimMode.Reference), new("maintenance:feed", ResourceClaimMode.Reference),
        new("maintenance:host-payload", ResourceClaimMode.Reference), new("maintenance:metadata", ResourceClaimMode.Mutation)
    ]);
    private readonly object _gate = new();
    private readonly RuntimeInstance _instance;
    private readonly InstanceLease _lease;
    private readonly StateDatabase _database;
    private readonly ResourceOperationLease _root;
    private readonly ResourceOperationBorrow _borrow;
    private readonly TreeReadLease _before;
    private readonly PreparedPayloadPlan _frozen;
    private readonly HostUpdateSource _source;
    private readonly OperationIdentity _identity;
    private readonly ContractReader _contracts;
    private readonly string _profile;
    private bool _disposed;

    private PreparedHostUpdate(RuntimeInstance instance, InstanceLease lease, StateDatabase database, ResourceOperationLease root,
        ResourceOperationBorrow borrow, TreeReadLease before, PreparedPayloadPlan frozen, HostUpdateSource source,
        OperationIdentity identity, ContractReader contracts, string profile)
    { _instance = instance; _lease = lease; _database = database; _root = root; _borrow = borrow; _before = before;
      _frozen = frozen; _source = source; _identity = identity; _contracts = contracts; _profile = profile; }

    internal static PreparedHostUpdate Prepare(RuntimeInstance instance, InstanceLease lease, StateDatabase database,
        ResourceLeaseManager resources, ResourceOperationLease root, HostUpdateRequest request,
        VerifiedUpdateCandidate candidate, PayloadApplyRoots roots, ContractReader contracts, CancellationToken cancellation)
    {
        lease.VerifyHeld(instance);
        var identity = UpdateRequestIdentity.Create(request);
        if (root.OperationId != request.OperationId || database.InstanceId != instance.InstanceId ||
            !StringComparer.OrdinalIgnoreCase.Equals(database.RuntimeRoot, instance.RuntimeRoot) ||
            candidate.Release.HostVersion != request.HostVersion) throw Invalid();
        var borrow = root.Borrow(resources, request.OperationId, PreparationClaims);
        TreeReadLease? held = null; PreparedPayloadPlan? frozen = null;
        try
        {
            return borrow.Use(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                var target = PhysicalPathPolicy.NormalizeWithoutLinks(roots.HostPayloadRoot);
                var parent = PhysicalObjectIdentity.Read(Path.GetDirectoryName(target)!);
                var blobs = new ImmutableBlobStore(instance, contracts);
                var before = new FileTreeArchive(new(SnapshotBudget.Default), blobs).CaptureApplication(target, cancellation);
                if (before.Snapshot.Shape != FileObjectKind.Directory) throw Invalid();
                _ = new UpdateManifestCodec(contracts).RequireOriginal(before, blobs, candidate.Manifest.Profile);
                held = TreeReadLease.AcquireApplication(target, parent, before);
                return candidate.Capture(root, blobs, before, cancellation, (feed, release, manifest, archive, tree) =>
                {
                    if (feed.Identity != request.Feed || release.HostVersion != request.HostVersion) throw Invalid();
                    var source = new HostUpdateSource(UpdateSourceCodec.Format, 1, instance.InstanceId, request.OperationId,
                        Guid.NewGuid(), feed, release.HostVersion, manifest, archive, request.Timing, request.Window)
                    {
                        Installation = new Installation.InstallationUpdateBinding(instance, contracts).Capture(target, before, candidate.Manifest.Profile)
                    };
                    var sourceReference = new UpdateSourceCodec(instance, contracts).Persist(source);
                    var plan = new FrozenPayloadApply(instance.InstanceId, source.OperationId, source.JournalId, JournalKind.UpdateApply,
                        PayloadAction.Upgrade, null, target, parent, before, tree, ["maintenance:host-payload"]) { MaintenanceSource = sourceReference };
                    frozen = new PayloadPlanCodec(instance, contracts, blobs, roots).Freeze(plan);
                    return new PreparedHostUpdate(instance, lease, database, root, borrow, held!, frozen, source, identity, contracts, candidate.Manifest.Profile);
                });
            });
        }
        catch { frozen?.Dispose(); held?.Dispose(); borrow.Dispose(); throw; }
    }

    internal T UseAcceptance<T>(RuntimeInstance instance, StateDatabase database,
        Func<OperationIdentity, FrozenPayloadApply, BlobReference, HostUpdateSource, T> use)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_instance != instance || !ReferenceEquals(_database, database)) throw Invalid();
            return _borrow.Use(() =>
            {
                _lease.VerifyHeld(instance);
                var plan = _frozen.Plan;
                new MaintenanceInstallation(instance, _contracts).RequireOriginal(_source, plan, _profile);
                if (!_root.IsActive || PhysicalObjectIdentity.Read(Path.GetDirectoryName(plan.TargetPath)!) != plan.ParentIdentity) throw Invalid();
                var actual = ApplicationTree.Capture(new(SnapshotBudget.Default), plan.TargetPath, default);
                if (actual.Shape != plan.Before.Snapshot.Shape || !actual.Entries.SequenceEqual(plan.Before.Snapshot.Entries) ||
                    !PhysicalTreeIdentity.Capture(plan.TargetPath, actual).SequenceEqual(plan.Before.OriginalObjects)) throw Invalid();
                return use(_identity, plan, _frozen.Reference, _source);
            });
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return; _disposed = true;
            try { _frozen.Dispose(); } finally { try { _before.Dispose(); } finally { _borrow.Dispose(); } }
        }
    }
    private static IOException Invalid() => new("maintenance_update_preparation_unbound");
}
