using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class PreparedExtensionActivation : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly ContractReader _contracts;
    private readonly PayloadApplyRoots _roots;
    private readonly ResourceOperationLease _root;
    private readonly ResourceOperationBorrow _borrow;
    private readonly ExtensionActivator.ApplyReservation _inventory;
    private readonly ActivationFileInputs _trees;
    private readonly BlobReference _originalPlan;
    private readonly BlobReference _physical;
    private readonly byte[] _manifest;
    private readonly InstalledActivationOrigin? _installedOrigin;
    private readonly ImmutableArray<FileTreeEntry> _files;
    private readonly ArchivedFileTree _runtimeTree;
    private readonly BlobReference _archive;
    private readonly CancellationToken _acceptedCancellation;
    private Task? _callback;
    private Task? _failedCallback;
    private ExtensionScope? _scope;
    private bool _committed;
    private Task? _dispose;
    internal string ExtensionId { get; }
    internal string TargetPath { get; }
    internal Guid OperationId => _root.OperationId;
    internal Guid JournalId { get; }
    internal BlobReference Source { get; }
    internal BlobReference PhysicalResult => _physical;
    internal ResourceOperationLease Root => _root;
    internal ContractReader Contracts => _contracts;
    internal CancellationToken AcceptedCancellation => _acceptedCancellation;
    internal void RequireReservation(ExtensionActivator.ApplyReservation inventory)
    { if (!ReferenceEquals(_inventory, inventory)) throw Invalid(); }

    private PreparedExtensionActivation(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ContractReader contracts, PayloadApplyRoots roots, ResourceOperationLease root, ResourceOperationBorrow borrow,
        IDisposable trees, BoundExtensionPayload bound, byte[] manifest, CancellationToken acceptedCancellation,
        ExtensionActivator.ApplyReservation inventory, InstalledActivationOrigin? installedOrigin = null)
    {
        _instance = instance; _database = database; _lease = lease; _contracts = contracts; _roots = roots;
        _installedOrigin = installedOrigin;
        _root = root; _borrow = borrow; _originalPlan = bound.PlanReference;
        _physical = bound.Physical[^1].Intent.Artifact; _manifest = manifest.ToArray();
        _acceptedCancellation = acceptedCancellation; _inventory = inventory;
        _files = bound.Plan.Candidate.Snapshot.Entries.Where(item => item.Kind == FileObjectKind.File).ToImmutableArray();
        _runtimeTree = bound.Plan.Candidate with
        {
            Snapshot = bound.Plan.Candidate.Snapshot with { Entries = Array.AsReadOnly(bound.Plan.Candidate.Snapshot.Entries.ToArray()) },
            OriginalObjects = bound.Physical[^1].Record.CandidateObjects,
            Contents = Array.AsReadOnly(bound.Plan.Candidate.Contents.ToArray())
        };
        ExtensionId = bound.Source.ExtensionId; TargetPath = bound.Plan.TargetPath; JournalId = bound.Plan.JournalId;
        Source = bound.Plan.ExtensionSource!; _archive = bound.Source.Package?.Archive ?? throw Invalid();
        _trees = new(trees, TargetPath, bound.Plan.ParentIdentity, _runtimeTree, _archive);
    }

    internal static PreparedExtensionActivation Prepare(RuntimeInstance instance, StateDatabase database,
        InstanceLease lease, ContractReader contracts, PayloadApplyRoots roots, PreparedExtensionPayload prepared,
        ExtensionActivator.ApplyReservation inventory, CancellationToken acceptedCancellation) =>
        prepared.WithPhysical<PreparedExtensionActivation>(inventory, acceptedCancellation, (plan, _, root, actualLease, policy, _, _, reference) =>
        {
            if (!ReferenceEquals(lease, actualLease) || !policy.MustActivateCandidate) throw Invalid();
            var bound = new ExtensionPayloadBinding(instance, database, contracts, roots).Read(plan.JournalId, reference);
            if (bound.Registered.Phase != JournalPhase.Applied || bound.Latest.Revision != bound.Registered.Revision ||
                bound.Preparation is not null || bound.Commit is not null) throw Invalid();
            var borrow = inventory.BorrowForActivation(root);
            IDisposable? held = null;
            try
            {
                var trees = new ExtensionTreeEffects(new(new(4096, 268435456)),
                    new(new(new(4096, 268435456)), new(instance, contracts)));
                held = trees.HoldInstalled(plan, bound.Physical[^1].Record);
                var entry = plan.Candidate.Snapshot.Entries.Single(item => item.RelativePath == "manifest.json");
                var bytes = File.ReadAllBytes(Path.Combine(plan.TargetPath, "manifest.json"));
                if (bytes.LongLength != entry.SizeBytes || Convert.ToHexStringLower(SHA256.HashData(bytes)) != entry.Sha256)
                    throw Invalid();
                _ = new ExtensionManifestAdmission(contracts).ReadCurrent(bytes, plan.ExtensionId!);
                return new(instance, database, lease, contracts, roots, root, borrow, held, bound, bytes, acceptedCancellation, inventory);
            }
            catch { held?.Dispose(); borrow.Dispose(); throw; }
        });

    internal static PreparedExtensionActivation PrepareInstalled(InstalledActivationOrigin origin,
        RuntimeInstance instance, StateDatabase database, InstanceLease lease, ContractReader contracts,
        PayloadApplyRoots roots, ResourceOperationLease root, ExtensionActivator.ApplyReservation inventory,
        CancellationToken acceptedCancellation)
    {
        origin.RequireOwner(instance, database, lease, root, inventory);
        var bound = origin.RequireCurrent();
        var borrow = inventory.BorrowForActivation(root);
        IDisposable? held = null;
        try
        {
            held = origin.HoldRuntimeFiles(out var manifest);
            return new(instance, database, lease, contracts, roots, root, borrow, held,
                bound, manifest, acceptedCancellation, inventory, origin);
        }
        catch { held?.Dispose(); borrow.Dispose(); throw; }
    }

    internal ExtensionManifest ReadManifest() => new ExtensionManifestAdmission(_contracts)
        .ReadCurrent(ReadManifestBytes(), ExtensionId);
    internal byte[] ReadManifestBytes() { RequireLifetime(); return _manifest.ToArray(); }

    internal InstalledNativeSource CaptureNativeSource()
    {
        RequireBeforeCallback();
        var tree = Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(new { _runtimeTree.Snapshot, _runtimeTree.OriginalObjects })));
        return new(ExtensionId, JournalId, _archive.Sha256, Convert.ToHexStringLower(SHA256.HashData(_manifest)), tree, TargetPath, PhysicalObjectIdentity.Read(TargetPath));
    }

    internal string ResolveFile(string relative)
    {
        RequireLifetime(); PhysicalPathPolicy.ValidateRelativePath(relative);
        if (!_files.Any(item => item.RelativePath == relative)) throw Invalid();
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.GetFullPath(Path.Combine(TargetPath, relative)));
        if (Path.GetRelativePath(TargetPath, path).Replace('\\', '/') != relative) throw Invalid();
        return path;
    }

    internal TreeReadLease.TreeBorrow HoldRuntimeFiles()
    {
        RequireLifetime();
        using var paths = _installedOrigin?.ProtectPaths();
        return _trees.Borrow();
    }

    internal ReadProviderPackage ReadPackageInputs(List<IDisposable> held)
    {
        using var paths = _installedOrigin?.ProtectPaths() ?? DirectoryAttributeScope.Hold(_instance);
        RequireLifetime();
        return _trees.ReadPackage(_instance, ExtensionId, _manifest, _contracts, held);
    }

    internal void RequirePortOwner(RuntimeInstance instance, StateDatabase database, InstanceLease lease, ExtensionScope scope)
    {
        RequireBeforeCallback();
        if (_instance != instance || !ReferenceEquals(_database, database) || !ReferenceEquals(_lease, lease)) throw Invalid();
        lock (_gate) if (!ReferenceEquals(_scope, scope) || _dispose is not null) throw Invalid();
    }

    internal InstalledPackageAssets CaptureAssets(ExtensionScope scope, InstalledExtensionConsumption consumption,
        TreeReadLease.TreeBorrow files)
    {
        RequireBeforeCallback();
        lock (_gate)
            if (!ReferenceEquals(_scope, scope) || _dispose is not null) throw Invalid();
        return new(this, scope, consumption, TargetPath, files, _runtimeTree);
    }

    internal ImmutableArray<string> AssemblyFiles() =>
        _files.Where(item => item.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(item => ResolveFile(item.RelativePath)).ToImmutableArray();

    internal void RequireBeforeCallback()
    {
        var bound = RequireApplied();
        if (_installedOrigin is null && bound.Preparation is not null) throw Invalid();
    }

    internal void RequireForPreparation(Guid activationId)
    {
        if (activationId == Guid.Empty) throw Invalid();
        var bound = RequireApplied();
        if (_installedOrigin is null && bound.Preparation is not null && bound.Preparation.ActivationId != activationId) throw Invalid();
    }

    internal void RequireForPublication(Guid activationId, BlobReference preparation)
    {
        var bound = RequireApplied();
        if (activationId == Guid.Empty || bound.Preparation?.ActivationId != activationId ||
            bound.PreparationIntent?.Artifact != preparation) throw Invalid();
    }

    internal void RequireForInstalledPublication()
    {
        if (_installedOrigin is null) throw Invalid();
        _ = RequireApplied();
    }

    internal BoundExtensionPayload RequireCommitted(Guid activationId)
    {
        RequireOwnerHeld();
        if (_installedOrigin is not null) return _borrow.Use(_installedOrigin.RequireCurrent);
        return _borrow.Use(() =>
        {
            var bound = new ExtensionPayloadBinding(_instance, _database, _contracts, _roots).Read(JournalId, _originalPlan);
            if (bound.Registered.Phase != JournalPhase.Committed || bound.Commit?.ActivationId != activationId ||
                bound.Commit.ActivationState != "published" || bound.Commit.PhysicalResult != _physical) throw Invalid();
            return bound;
        });
    }

    internal InstalledConsumptionFacts CaptureConsumptionFacts()
    {
        RequireBeforeCallback();
        ExtensionScope scope;
        lock (_gate) scope = _scope ?? throw Invalid();
        return new InstalledConsumptionFacts(this, scope.ActivationId);
    }

    internal sealed class InstalledConsumptionFacts
    {
        private readonly RuntimeInstance _instance;
        private readonly StateDatabase _database;
        private readonly InstanceLease _lease;
        private readonly ContractReader _contracts;
        private readonly PayloadApplyRoots _roots;
        private readonly BlobReference _plan, _source, _physical;
        private readonly Guid _journal, _activation;
        private readonly string _extension;
        private readonly bool _existingInstallation;

        internal InstalledConsumptionFacts(PreparedExtensionActivation owner, Guid activation)
        {
            owner.RequireBeforeCallback();
            lock (owner._gate)
                if (owner._scope?.ActivationId != activation || owner._dispose is not null) throw Invalid();
            _instance = owner._instance; _database = owner._database; _lease = owner._lease;
            _contracts = owner._contracts; _roots = owner._roots; _plan = owner._originalPlan;
            _source = owner.Source; _physical = owner._physical; _journal = owner.JournalId;
            _activation = activation; _extension = owner.ExtensionId;
            _existingInstallation = owner._installedOrigin is not null;
        }

        internal void PublishCurrent(ResourceLeaseManager resources, RecoveryIndex recovery) =>
            ExtensionProjectionPublication.PublishIfDirty(_instance, _database, _lease, _contracts, _roots, resources, recovery);

        internal InstallationReadLease? OpenReadLease() => InstallationReadLease.TryAcquire(
            _instance, _database, _lease, _contracts, _roots, _journal, ReadCurrent);

        internal void RequireCurrent() => _ = ReadCurrent();

        private BoundExtensionPayload ReadCurrent()
        {
            _lease.VerifyHeld(_instance);
            var bound = new ExtensionPayloadBinding(_instance, _database, _contracts, _roots).Read(_journal, _plan);
            if (bound.Registered.Phase != JournalPhase.Committed || bound.Commit is not { } commit ||
                bound.Footprint is not { IsInstalled: true } footprint || commit.Source != _source ||
                commit.PhysicalResult != _physical || !_existingInstallation &&
                (commit.ActivationId != _activation || commit.ActivationState != "published" || !commit.DesiredEnabled)) throw Unavailable();
            var expected = ExtensionPayloadBinding.DerivedInstallation(bound.Plan, commit, footprint);
            using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
            using var transaction = connection.BeginTransaction(deferred: true);
            if (ExtensionInstalledRows.ReadCurrentMetadata(connection, transaction, _extension) != expected ||
                HostSettingsRows.ReadCurrent(connection, transaction).Values.Extensions
                    .SingleOrDefault(item => item.ExtensionId == _extension)?.Enabled != true) throw Unavailable();
            transaction.Commit();
            return bound;
        }

        private static IOException Unavailable() => new("extension_installation_consumption_unavailable");
    }

    internal void RegisterCallback(ExtensionScope scope, Task callback)
    {
        _inventory.RequireCandidate(scope);
        lock (_gate)
        {
            if (_dispose is not null || _callback is not null) throw Invalid();
            _callback = callback; _scope = scope;
        }
        Observe(callback);
    }

    internal BoundExtensionPayload AcceptCommitted(ExtensionScope scope)
    {
        _inventory.RequireCandidate(scope); RequireOwnerHeld();
        var bound = RequireCommitted(scope.ActivationId);
        lock (_gate)
        {
            if (_dispose is not null || !ReferenceEquals(_scope, scope) ||
                _callback?.IsCompletedSuccessfully != true || !scope.Activation.IsPublished || !scope.Activation.IsAccepting)
                throw Invalid();
            _committed = true;
        }
        return bound;
    }

    internal BoundExtensionPayload ReadAppliedInsidePublication()
    {
        using var paths = DirectoryAttributeScope.Hold(_instance);
        using var blobs = BlobReadScope.Hold(_instance);
        RequireLifetime();
        var bound = new ExtensionPayloadBinding(_instance, _database, _contracts, _roots).Read(JournalId, _originalPlan);
        if (bound.Registered.Phase != JournalPhase.Applied || bound.Latest.Revision != bound.Registered.Revision ||
            bound.Commit is not null || bound.Physical[^1].Intent.Artifact != _physical) throw Invalid();
        _ = new JournalWriterStore(_database).RequireCurrentExecutor(JournalId);
        _database.Write((connection, transaction) =>
        {
            PayloadEffectCursor.RequireInside(connection, transaction, bound.Registered, bound.Source, bound.Identity);
            return true;
        });
        return bound;
    }

    internal void RequireLifetime()
    {
        _acceptedCancellation.ThrowIfCancellationRequested(); RequireOwnerHeld();
    }

    internal void RequireOwnerHeld()
    {
        lock (_gate) if (_dispose is not null) throw new ObjectDisposedException(nameof(PreparedExtensionActivation));
        _lease.VerifyHeld(_instance);
        if (!_root.IsActive) throw Invalid();
        _borrow.VerifyHeld();
    }

    private BoundExtensionPayload RequireApplied()
    {
        RequireLifetime();
        if (_installedOrigin is not null) return _borrow.Use(_installedOrigin.RequireCurrent);
        return _borrow.Use(() => _lease.PublishExecution(ReadAppliedInsidePublication));
    }

    internal void RecordCallbackFailure(Task callback, Exception observed)
    {
        lock (_gate)
        {
            // A timeout can race with a later task fault; only the observed task failure permits restoration.
            if (_dispose is not null || !ReferenceEquals(_callback, callback) || !callback.IsFaulted ||
                !ReferenceEquals(callback.Exception?.InnerException, observed)) return;
            _failedCallback = callback;
        }
    }

    internal FailedCallback? CaptureFailedCallback()
    {
        ExtensionScope scope;
        lock (_gate)
        {
            if (_installedOrigin is not null || _committed || _callback?.IsFaulted != true ||
                !ReferenceEquals(_callback, _failedCallback) ||
                _dispose?.IsCompletedSuccessfully != true || _scope is null) return null;
            scope = _scope;
        }
        _lease.VerifyHeld(_instance);
        _inventory.RequireCandidate(scope); _inventory.RequireNativeUnmapped();
        if (!_root.IsActive || scope.Activation.IsPublished || scope.Activation.IsAccepting ||
            !scope.CleanupCompletedSuccessfully) throw Invalid();
        var borrow = _inventory.BorrowForActivation(_root);
        try
        {
            var bound = new ExtensionPayloadBinding(_instance, _database, _contracts, _roots).Read(JournalId, _originalPlan);
            if (bound.Registered.Phase != JournalPhase.Applied || bound.Latest.Phase != JournalPhase.Applied ||
                bound.Registered.Revision != bound.Latest.Revision || bound.Preparation is not null ||
                bound.Commit is not null || !bound.Restoration.IsEmpty ||
                bound.Physical[^1].Intent.Artifact != _physical) throw Invalid();
            var writer = JournalWriterStore.CurrentWriter;
            if (new JournalWriterStore(_database).RequireCurrentExecutor(JournalId) != writer.WriterId) throw Invalid();
            return FailedCallback.Capture(this, borrow);
        }
        catch { borrow.Dispose(); throw; }
    }

    internal sealed class FailedCallback : IDisposable
    {
        private readonly PreparedExtensionActivation _owner;
        private ResourceOperationBorrow? _borrow;
        internal ActivationFailureFacts Facts { get; }
        internal Guid JournalId => _owner.JournalId;
        internal BlobReference Plan => _owner._originalPlan;

        private FailedCallback(PreparedExtensionActivation owner, ResourceOperationBorrow borrow,
            ActivationFailureFacts facts)
        { _owner = owner; _borrow = borrow; Facts = facts; }

        internal static FailedCallback Capture(PreparedExtensionActivation owner, ResourceOperationBorrow borrow)
        {
            var value = new FailedCallback(owner, borrow, new(owner._scope!.ActivationId, JournalWriterStore.CurrentWriter));
            value.Require(owner._instance, owner._database, owner._lease, owner.JournalId);
            return value;
        }

        internal void Require(RuntimeInstance instance, StateDatabase database, InstanceLease lease, Guid journal)
        {
            if (!ReferenceEquals(instance, _owner._instance) || !ReferenceEquals(database, _owner._database) ||
                !ReferenceEquals(lease, _owner._lease) || journal != _owner.JournalId) throw Invalid();
            _owner._lease.VerifyHeld(instance);
            var borrow = Volatile.Read(ref _borrow) ?? throw Invalid();
            borrow.VerifyHeld();
            ExtensionScope scope;
            lock (_owner._gate)
            {
                if (_owner._committed || _owner._installedOrigin is not null ||
                    _owner._callback?.IsFaulted != true || !ReferenceEquals(_owner._callback, _owner._failedCallback) ||
                    _owner._dispose?.IsCompletedSuccessfully != true ||
                    _owner._scope?.ActivationId != Facts.ActivationId) throw Invalid();
                scope = _owner._scope ?? throw Invalid();
            }
            _owner._inventory.RequireCandidate(scope);
            if (!_owner._root.IsActive || scope.Activation.IsPublished || scope.Activation.IsAccepting ||
                !scope.CleanupCompletedSuccessfully) throw Invalid();
        }

        public void Dispose() => Interlocked.Exchange(ref _borrow, null)?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Task task;
        lock (_gate) task = _dispose ??= DisposeCoreAsync();
        return new(task);
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        Task? callback; ExtensionScope? scope; bool committed;
        lock (_gate) { callback = _callback; scope = _scope; committed = _committed; }
        var cleanup = !committed && scope is not null ? scope.DisposeAsync().AsTask() : Task.CompletedTask;
        Observe(cleanup);
        if (callback is not null)
        {
            try { await callback.ConfigureAwait(false); }
            catch { }
        }
        await cleanup.ConfigureAwait(false);
        try { _trees.Dispose(); }
        finally { try { _borrow.Dispose(); } finally { _installedOrigin?.Dispose(); } }
    }

    private static void Observe(Task task) => _ = task.ContinueWith(static failure => { _ = failure.Exception; },
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private static IOException Invalid() => new("extension_activation_source_unbound");
}
