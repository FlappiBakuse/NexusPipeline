using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class PreparedExtensionPayload : IDisposable
{
    private readonly object _gate = new();
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly IRecoveryIndex _recovery;
    private readonly ResourceOperationLease _root;
    private readonly ResourceOperationBorrow _borrow;
    private readonly StableDirectoryLease _parent;
    private readonly VerifiedExtensionPackage.Candidate? _candidate;
    private readonly ImmutableBlobStore _blobs;
    private readonly ContractReader _contracts;
    private readonly PayloadApplyRoots _roots;
    private readonly FrozenPayloadApply _plan;
    private readonly ExtensionPayloadSource _source;
    private readonly OperationIdentity _identity;
    private readonly BlobReference _planReference;
    private readonly ProcessIdentityLease _executor;
    private bool _disposed;

    private PreparedExtensionPayload(RuntimeInstance instance, StateDatabase database, InstanceLease lease, IRecoveryIndex recovery,
        ResourceOperationLease root, ResourceOperationBorrow borrow, StableDirectoryLease parent,
        VerifiedExtensionPackage.Candidate? candidate, ImmutableBlobStore blobs, ContractReader contracts, PayloadApplyRoots roots,
        FrozenPayloadApply plan, ExtensionPayloadSource source, OperationIdentity identity, BlobReference planReference)
    {
        _instance = instance; _database = database; _lease = lease; _root = root; _borrow = borrow;
        _recovery = recovery; _parent = parent; _candidate = candidate; _blobs = blobs; _contracts = contracts; _roots = roots;
        _plan = plan; _source = source; _identity = identity; _planReference = planReference;
        _executor = lease.ExecutorIdentity;
    }

    [SupportedOSPlatform("windows")]
    internal static PreparedExtensionPayload PrepareFirstInstall(RuntimeInstance instance, StateDatabase database,
        InstanceLease lease, ResourceLeaseManager resources, IRecoveryIndex recovery, ResourceOperationLease root,
        VerifiedExtensionPackage.Candidate candidate, PayloadApplyRoots roots, ContractReader contracts,
        string subject, CancellationToken cancellation) =>
        Prepare(instance, database, lease, resources, recovery, root, PayloadAction.Install,
            candidate.Binding.Entry.ExtensionId, 0, new HostSettingsStore(database).Read().Revision,
            candidate, roots, contracts, subject, cancellation);

    [SupportedOSPlatform("windows")]
    internal static PreparedExtensionPayload Prepare(RuntimeInstance instance, StateDatabase database,
        InstanceLease lease, ResourceLeaseManager resources, IRecoveryIndex recovery, ResourceOperationLease root,
        PayloadAction action, string extensionId, long expectedInstalledRevision, long expectedSettingsRevision,
        VerifiedExtensionPackage.Candidate? candidate, PayloadApplyRoots roots, ContractReader contracts,
        string subject, CancellationToken cancellation)
    {
        lease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId ||
            !StringComparer.OrdinalIgnoreCase.Equals(database.RuntimeRoot, instance.RuntimeRoot) ||
            !resources.UsesRecoveryIndex(recovery) || !Enum.IsDefined(action) ||
            expectedInstalledRevision is < 0 or >= 9007199254740991 ||
            (action == PayloadAction.Remove) != (candidate is null) ||
            candidate is not null && (candidate.Binding.InstanceId != instance.InstanceId ||
                root.OperationId != candidate.Binding.OperationId || candidate.Binding.Entry.ExtensionId != extensionId))
            throw Invalid();
        if (subject is not { Length: >= 1 and <= 256 } || subject.Any(char.IsControl)) throw Invalid();
        _ = new ExtensionApplyRequest(action, extensionId, expectedInstalledRevision, expectedSettingsRevision,
            candidate?.Binding.Catalog, candidate?.Binding.Entry).Identity(root.OperationId, subject);
        var original = new ExtensionInstalledEvidence(instance, database, contracts, roots).ReadCurrent(extensionId);
        if ((original?.Metadata.Revision ?? 0) != expectedInstalledRevision ||
            (action == PayloadAction.Install ? original?.Metadata.IsInstalled == true : original?.Metadata.IsInstalled != true))
            throw Invalid();
        var target = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(roots.ExtensionPayloadRoot, extensionId));
        var parentPath = Path.GetDirectoryName(target)!;
        var workspace = ExtensionWorkspacePath.For(target, root.OperationId);
        var settings = new HostSettingsStore(database).ReadExtensionIntent(extensionId, expectedInstalledRevision == 0);
        if (settings.SettingsRevision != expectedSettingsRevision ||
            settings.InitializeEnabledPreference && settings.SettingsRevision >= 9007199254740991) throw Invalid();
        var fixedClaims = new List<ResourceClaim>
        {
            new("extension:" + extensionId, ResourceClaimMode.Mutation),
            new("blobs:content", ResourceClaimMode.Reference),
            new("settings:host", settings.InitializeEnabledPreference ? ResourceClaimMode.Mutation : ResourceClaimMode.Reference)
        };
        if (candidate is not null) fixedClaims.Add(new("extensions:catalog", ResourceClaimMode.Reference));
        var claims = PhysicalResourceAdmission.Merge(fixedClaims.Concat(PhysicalResourceAdmission.Read(target))
            .Concat(PhysicalResourceAdmission.Read(workspace)));
        var borrow = root.Borrow(resources, root.OperationId, claims);
        StableDirectoryLease? parent = null;
        try
        {
            return borrow.Use(() =>
            {
                parent = StableDirectoryLease.Acquire(parentPath);
                var parentIdentity = PhysicalObjectIdentity.Read(parentPath);
                var before = original?.Footprint.Payload ?? Missing();
                if (original is not null && (original.Footprint.TargetPath != target ||
                    original.Footprint.ParentIdentity != parentIdentity)) throw Invalid();
                using var held = TreeReadLease.Acquire(target, parentIdentity, before);
                RequireBefore();
                var blobs = new ImmutableBlobStore(instance, contracts);
                T WithCandidate<T>(Func<ExtensionPackageBinding?, ArchivedFileTree, T> use) =>
                    candidate is null ? use(null, Missing()) : candidate.Use(root, blobs, cancellation, (binding, tree) => use(binding, tree));
                return WithCandidate((binding, tree) =>
                {
                    var packageIdentity = binding is null ? null : new NexusPipeline.Core.Extensions.ExtensionPackageIdentity(
                        extensionId, binding.Entry.PackageVersion, binding.Entry.Kind == "managed-code"
                            ? NexusPipeline.Core.Extensions.ExtensionPackageKind.ManagedCode
                            : NexusPipeline.Core.Extensions.ExtensionPackageKind.DataSpecialized,
                        binding.Entry.ArtifactSha256, binding.Entry.ManifestSha256);
                    _ = NexusPipeline.Core.Extensions.ExtensionApplyPolicy.Freeze(new(action switch
                    {
                        PayloadAction.Install => NexusPipeline.Core.Extensions.ExtensionApplyAction.Install,
                        PayloadAction.Upgrade => NexusPipeline.Core.Extensions.ExtensionApplyAction.Upgrade,
                        PayloadAction.Remove => NexusPipeline.Core.Extensions.ExtensionApplyAction.Remove,
                        _ => throw Invalid()
                    }, extensionId, expectedInstalledRevision, packageIdentity), original is null ? null :
                        new(original.Metadata.Revision, original.Metadata.IsInstalled, original.Metadata.Package),
                        settings.DesiredEnabled, NexusPipeline.Core.Extensions.ExtensionObservedActivity.None,
                        ExtensionUpgradeSource.AllowsSameVersion(binding?.Catalog.Channel, original?.Operation.Source.Package?.Catalog.Identity.Channel));
                    ExtensionCatalogSaved? catalog = null;
                    using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Extensions | DatabaseDomain.System))
                    using (var transaction = connection.BeginTransaction(deferred: true))
                    {
                        ExtensionInstalledRows.RequireExpectedInside(connection, transaction, extensionId,
                            expectedInstalledRevision, original?.Metadata.Footprint);
                        if (binding is not null)
                        {
                            catalog = ExtensionCatalogRows.ReadHead(connection, transaction, binding.Catalog.Channel) ?? throw Invalid();
                            if (catalog.Identity != binding.Catalog || catalog.KeyId != binding.CatalogKeyId) throw Invalid();
                        }
                        HostSettingsStore.RequireExtensionIntentInside(connection, transaction, extensionId, settings);
                        transaction.Commit();
                    }
                    CatalogPackageSource? package = null;
                    if (binding is not null)
                    {
                        var archive = new BlobReference($"blobs/content/{binding.Entry.ArtifactSha256}.bin",
                            binding.Entry.ArtifactBytes, binding.Entry.ArtifactSha256);
                        if (binding.ArchivePath != Path.Combine(instance.RuntimeRoot, archive.RelativePath.Replace('/', Path.DirectorySeparatorChar)))
                            archive = blobs.PersistContent(binding.ArchivePath,
                                new(Path.GetFileName(binding.ArchivePath), FileObjectKind.File,
                                    binding.Entry.ArtifactBytes, binding.Entry.ArtifactSha256), cancellation);
                        package = new(catalog!, binding.Entry, archive);
                    }
                    var source = new ExtensionPayloadSource(PayloadSourceCodec.Format, 1, instance.InstanceId,
                        root.OperationId, Guid.NewGuid(), extensionId, action, expectedInstalledRevision, original?.Metadata.Footprint,
                        package, settings.SettingsRevision, settings.DesiredEnabled, claims, settings.InitializeEnabledPreference);
                    var sourceReference = new PayloadSourceCodec(instance, contracts).Persist(source);
                    var plan = new FrozenPayloadApply(instance.InstanceId, root.OperationId, source.JournalId,
                        JournalKind.ExtensionApply, action, extensionId, target, parentIdentity, before, tree,
                        claims.Select(item => item.ResourceKey).ToImmutableArray()) { ExtensionSource = sourceReference };
                    ExtensionPhysicalCodec.RequireCapacity(plan);
                    PayloadMetadataCapacity.Require(plan, source);
                    using var frozen = new PayloadPlanCodec(instance, contracts, blobs, roots).Freeze(plan);
                    RequireBefore();
                    var identity = ExtensionApplyRequest.FromSource(source).Identity(root.OperationId, subject);
                    return new PreparedExtensionPayload(instance, database, lease, recovery, root, borrow, parent!,
                        candidate, blobs, contracts, roots, frozen.Plan, source, identity, frozen.Reference);
                });
                void RequireBefore()
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!root.IsActive || PhysicalPathPolicy.NormalizeWithoutLinks(target) != target ||
                        PhysicalObjectIdentity.Read(parentPath) != parentIdentity ||
                        File.Exists(workspace) || Directory.Exists(workspace)) throw Invalid();
                }
            });
        }
        catch { parent?.Dispose(); borrow.Dispose(); throw; }
    }

    private static ArchivedFileTree Missing() => new(new(FileObjectKind.Missing, []), [], []);

    [SupportedOSPlatform("windows")]
    private T UseCandidate<T>(CancellationToken cancellation, Func<ExtensionPackageBinding?, T> use) =>
        _candidate is null ? use(null) : _candidate.Use(_root, _blobs, cancellation, (binding, _) => use(binding));

    [SupportedOSPlatform("windows")]
    internal T UseAcceptance<T>(RuntimeInstance instance, StateDatabase database, PayloadApplyRoots roots,
        Func<OperationIdentity, FrozenPayloadApply, ExtensionPayloadSource, T> accept)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (instance != _instance || database.InstanceId != _database.InstanceId ||
                !StringComparer.OrdinalIgnoreCase.Equals(database.DatabasePath, _database.DatabasePath) ||
                roots != _roots || !_root.IsActive) throw Invalid();
            _lease.VerifyHeld(instance);
            return _borrow.Use(() => UseCandidate(CancellationToken.None, binding =>
                _lease.PublishExecution(() =>
                {
                    if (!_root.IsActive || binding?.Entry != _source.Package?.Entry) throw Invalid();
                    _lease.VerifyHeld(instance);
                    RequireRecovery(_recovery.Current, _source.Claims, _source.JournalId);
                    RequirePhysicalBefore();
                    return accept(_identity, _plan, _source);
                })));
        }
    }

    [SupportedOSPlatform("windows")]
    internal T WithPhysical<T>(ExtensionActivator.ApplyReservation inventory, CancellationToken acceptedCancellation,
        Func<FrozenPayloadApply, ExtensionPayloadSource, ResourceOperationLease, InstanceLease,
            NexusPipeline.Core.Extensions.ExtensionApplyPlan, OperationIdentity, Guid?, BlobReference, T> action)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            acceptedCancellation.ThrowIfCancellationRequested(); _lease.VerifyHeld(_instance);
            var recovery = _recovery.Current;
            if (recovery.GlobalBlockReason is not null || recovery.Isolations.Any(item => item.JournalId != _source.JournalId &&
                    _source.Claims.Any(claim => claim.ResourceKey == item.ResourceKey &&
                        (claim.Mode == ResourceClaimMode.Mutation || item.Mode == ResourceClaimMode.Mutation))) ||
                recovery.ResourceHolds.Any(item => _source.Claims.Any(claim => claim.ResourceKey == item.ResourceKey &&
                    (claim.Mode == ResourceClaimMode.Mutation || item.Mode == ResourceClaimMode.Mutation))))
                throw new IOException("extension_apply_recovery_pending");
            return _borrow.Use(() => inventory.Use(_root, (activity, closure, activationId) =>
                UseCandidate(acceptedCancellation, binding =>
                {
                    if (!_root.IsActive || binding?.Entry != _source.Package?.Entry) throw Invalid();
                    var registered = new RecoveryJournalRegistry(_database).ReadJournal(_plan.JournalId);
                    if (registered.Kind != JournalKind.ExtensionApply || registered.OperationId != _identity.OperationId ||
                        registered.PlanPath != _planReference.RelativePath || registered.PlanDigest != _planReference.Sha256)
                        throw Invalid();
                    _ = new JournalWriterStore(_database).RequireCurrentExecutor(registered.JournalId, _executor);
                    var package = binding is null ? null : new NexusPipeline.Core.Extensions.ExtensionPackageIdentity(_source.ExtensionId,
                        binding.Entry.PackageVersion, binding.Entry.Kind == "managed-code"
                            ? NexusPipeline.Core.Extensions.ExtensionPackageKind.ManagedCode
                            : NexusPipeline.Core.Extensions.ExtensionPackageKind.DataSpecialized,
                        binding.Entry.ArtifactSha256, binding.Entry.ManifestSha256);
                    var original = new ExtensionInstalledEvidence(_instance, _database, _contracts, _roots).ReadCurrent(_source.ExtensionId);
                    if ((original?.Metadata.Revision ?? 0) != _source.ExpectedInstalledRevision ||
                        original?.Metadata.Footprint != _source.OriginalFootprint) throw Invalid();
                    if (original is not null)
                    {
                        var stopped = new JournalWriterStore(_database).ObserveForCurrentExecutor(original.Metadata.JournalId, _executor);
                        if (!stopped.IsComplete || stopped.State != NexusPipeline.Core.Execution.WriterState.Stopped)
                            throw new IOException("extension_original_writer_stop_unconfirmed");
                    }
                    var policy = NexusPipeline.Core.Extensions.ExtensionApplyPolicy.Freeze(new(
                        _source.Action switch
                        {
                            PayloadAction.Install => NexusPipeline.Core.Extensions.ExtensionApplyAction.Install,
                            PayloadAction.Upgrade => NexusPipeline.Core.Extensions.ExtensionApplyAction.Upgrade,
                            PayloadAction.Remove => NexusPipeline.Core.Extensions.ExtensionApplyAction.Remove,
                            _ => throw Invalid()
                        }, _source.ExtensionId, _source.ExpectedInstalledRevision, package),
                        original is null ? null : new(original.Metadata.Revision, original.Metadata.IsInstalled, original.Metadata.Package),
                        _source.DesiredEnabled, activity,
                        ExtensionUpgradeSource.AllowsSameVersion(binding?.Catalog.Channel, original?.Operation.Source.Package?.Catalog.Identity.Channel));
                    if (policy.MustDeactivateExistingScope
                            ? closure != NexusPipeline.Core.Extensions.ExtensionScopeClosure.Stopped
                            : closure != NexusPipeline.Core.Extensions.ExtensionScopeClosure.NotRequired)
                        throw Invalid();
                    return action(_plan, _source, _root, _lease, policy, _identity, activationId, _planReference);
                })));
        }
    }

    internal string ExtensionId => _source.ExtensionId;

    [SupportedOSPlatform("windows")]
    internal ResourceOperationLease RequireReservationRoot()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _lease.VerifyHeld(_instance);
            if (!_root.IsActive) throw Invalid();
            return _borrow.Use(() => _lease.PublishExecution(() =>
            {
                var bound = new ExtensionPayloadBinding(_instance, _database, _contracts, _roots).Read(_plan.JournalId, _planReference);
                if (bound.Identity != _identity || bound.Commit is not null) throw Invalid();
                _ = new JournalWriterStore(_database).RequireCurrentExecutor(_plan.JournalId, _executor);
                _database.Write((connection, transaction) =>
                {
                    PayloadEffectCursor.RequireInside(connection, transaction, bound.Registered, _source, _identity);
                    return true;
                });
                return _root;
            }));
        }
    }

    internal BoundExtensionPayload? ReadCommitted()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _lease.VerifyHeld(_instance);
            return _borrow.Use<BoundExtensionPayload?>(() =>
            {
                var registered = new RecoveryJournalRegistry(_database).ReadJournal(_plan.JournalId);
                if (registered.Phase != JournalPhase.Committed) return null;
                var bound = new ExtensionPayloadBinding(_instance, _database, _contracts, _roots).Read(_plan.JournalId, _planReference);
                if (bound.Identity != _identity) throw Invalid();
                return bound;
            });
        }
    }

    internal static void RequireCurrentInside(SqliteConnection connection, SqliteTransaction transaction,
        ExtensionPayloadSource source)
    {
        ExtensionInstalledRows.RequireExpectedInside(connection, transaction, source.ExtensionId,
            source.ExpectedInstalledRevision, source.OriginalFootprint);
        if (source.Package is { } package &&
            ExtensionCatalogRows.ReadHead(connection, transaction, package.Catalog.Identity.Channel) != package.Catalog) throw Invalid();
        var saved = RecoveryJournalRegistry.ReadValidatedSnapshotInside(connection, transaction);
        RequireRecovery(new(saved.IsReady, saved.Revision, null, saved.Isolations), source.Claims, source.JournalId);
        HostSettingsStore.RequireExtensionIntentInside(connection, transaction, source.ExtensionId,
            new(source.SettingsRevision, source.DesiredEnabled, source.InitializeEnabledPreference));
    }

    private static void RequireRecovery(RecoveryProjection current, IReadOnlyList<ResourceClaim> claims, Guid ownJournal)
    {
        if (!current.IsReady || current.GlobalBlockReason is not null ||
            current.Isolations.Any(isolation => isolation.JournalId != ownJournal && claims.Any(claim => claim.ResourceKey == isolation.ResourceKey &&
                (claim.Mode == ResourceClaimMode.Mutation || isolation.Mode == ResourceClaimMode.Mutation))) ||
            current.ResourceHolds.Any(hold => claims.Any(claim => claim.ResourceKey == hold.ResourceKey &&
                (claim.Mode == ResourceClaimMode.Mutation || hold.Mode == ResourceClaimMode.Mutation))))
            throw new IOException("extension_apply_recovery_pending");
    }

    private void RequirePhysicalBefore()
    {
        var parent = Path.GetDirectoryName(_plan.TargetPath)!;
        if (PhysicalPathPolicy.NormalizeWithoutLinks(_plan.TargetPath) != _plan.TargetPath ||
            PhysicalObjectIdentity.Read(parent) != _plan.ParentIdentity ||
            File.Exists(ExtensionWorkspacePath.For(_plan)) || Directory.Exists(ExtensionWorkspacePath.For(_plan))) throw Invalid();
        using var held = TreeReadLease.Acquire(_plan.TargetPath, _plan.ParentIdentity, _plan.Before);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _parent.Dispose(); }
            finally { _borrow.Dispose(); }
        }
    }

    private static IOException Invalid() => new("extension_apply_preparation_unbound");
}
