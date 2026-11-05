using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class InstalledActivationOrigin : IDisposable
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly ContractReader _contracts;
    private readonly PayloadApplyRoots _roots;
    private readonly ExtensionCatalogStore _catalog;
    private readonly ResourceOperationLease _root;
    private readonly ResourceOperationBorrow _borrow;
    private readonly ExtensionActivator.ApplyReservation _inventory;
    private readonly ExtensionInstalledMetadata _installed;
    private readonly RecoveryIndex _recovery;
    private readonly IReadOnlyList<ResourceClaim> _claims;
    private readonly BlobReference _plan;
    private readonly long _settingsRevision;
    private InstallationReadLease? _evidence;
    private int _disposed;
    internal long InstalledRevision => _installed.Revision;

    private InstalledActivationOrigin(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ContractReader contracts, PayloadApplyRoots roots, ExtensionCatalogStore catalog, ResourceOperationLease root,
        ResourceOperationBorrow borrow, ExtensionActivator.ApplyReservation inventory, BoundExtensionInstallation installed,
        RecoveryIndex recovery, IReadOnlyList<ResourceClaim> claims, long settingsRevision)
    {
        _instance = instance; _database = database; _lease = lease; _contracts = contracts; _roots = roots;
        _catalog = catalog; _root = root; _borrow = borrow; _inventory = inventory;
        _installed = installed.Metadata; _plan = installed.Operation.PlanReference;
        _recovery = recovery; _claims = Array.AsReadOnly(claims.ToArray()); _settingsRevision = settingsRevision;
    }

    internal static InstalledActivationOrigin Prepare(RuntimeInstance instance, StateDatabase database,
        InstanceLease lease, ContractReader contracts, PayloadApplyRoots roots, ExtensionCatalogStore catalog,
        ResourceLeaseManager resources, RecoveryIndex recovery, ResourceOperationLease root, ExtensionActivator.ApplyReservation inventory,
        string extensionId)
    {
        lease.VerifyHeld(instance);
        using var reads = DatabaseReadScope.Hold(database.DatabasePath);
        if (catalog.Instance != instance || !resources.UsesRecoveryIndex(recovery)) throw Invalid();
        var reader = new ExtensionInstalledEvidence(instance, database, contracts, roots);
        InstallationReadLease? evidence = reader.ProtectCurrent(extensionId, lease);
        try
        {
            var bound = evidence?.ReadCurrent();
            var installed = bound is null ? reader.ReadCurrent(extensionId) ?? throw Invalid() :
                new BoundExtensionInstallation(ExtensionPayloadBinding.DerivedInstallation(bound.Plan, bound.Commit!, bound.Footprint!),
                    bound.Footprint!, bound);
            if (!installed.Metadata.IsInstalled) throw Invalid();
            using var paths = evidence?.ProtectPaths();
            var claims = Claims(instance, installed);
            var settingsRevision = new HostSettingsStore(database).Read().Revision;
            var borrow = root.Borrow(resources, root.OperationId, claims);
            var result = new InstalledActivationOrigin(instance, database, lease, contracts, roots,
                catalog, root, borrow, inventory, installed, recovery, claims, settingsRevision) { _evidence = evidence };
            evidence = null;
            try { _ = result.RequireCurrent(); return result; }
            catch { result.Dispose(); throw; }
        }
        finally { evidence?.Dispose(); }
    }

    internal static IReadOnlyList<ResourceClaim> Claims(RuntimeInstance instance, BoundExtensionInstallation installed)
    {
        if (!installed.Metadata.IsInstalled || installed.Operation.Source.Package is not { } package) throw Invalid();
        var archive = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, package.Archive.RelativePath));
        return PhysicalResourceAdmission.Merge(new ResourceClaim[]
        {
            new("extension:" + installed.Metadata.ExtensionId, ResourceClaimMode.Mutation),
            new("extensions:catalog", ResourceClaimMode.Reference), new("settings:host", ResourceClaimMode.Reference),
            new("blobs:content", ResourceClaimMode.Reference)
        }.Concat(PhysicalResourceAdmission.Read(installed.Footprint.TargetPath, ResourceClaimMode.Reference))
            .Concat(PhysicalResourceAdmission.Read(archive, ResourceClaimMode.Reference, includeDescendants: false)));
    }

    internal void RequireOwner(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ResourceOperationLease root, ExtensionActivator.ApplyReservation inventory)
    {
        if (!ReferenceEquals(instance, _instance) || !ReferenceEquals(database, _database) ||
            !ReferenceEquals(lease, _lease) || !ReferenceEquals(root, _root) || !ReferenceEquals(inventory, _inventory)) throw Invalid();
    }

    internal BoundExtensionPayload RequireCurrent()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var reads = DatabaseReadScope.Hold(_database.DatabasePath);
        using var paths = _evidence?.ProtectPaths();
        _lease.VerifyHeld(_instance);
        var current = _recovery.Current;
        if (!current.IsReady || current.GlobalBlockReason is not null ||
            current.Isolations.Any(item => Conflicts(item.ResourceKey, item.Mode)) ||
            current.ResourceHolds.Any(item => Conflicts(item.ResourceKey, item.Mode))) throw Invalid();
        return _borrow.Use(() => _inventory.Use(_root, (_, _, _) =>
        {
            var bound = _evidence?.ReadCurrent() ?? new ExtensionPayloadBinding(_instance, _database, _contracts, _roots)
                .Read(_installed.JournalId, _plan);
            if (bound.Commit is null || bound.Footprint is null ||
                ExtensionPayloadBinding.DerivedInstallation(bound.Plan, bound.Commit, bound.Footprint) != _installed ||
                bound.Registered.Phase != JournalPhase.Committed || bound.Source.Package is not { } package) throw Invalid();
            _catalog.RequireInstalledHistory(_root, package.Catalog, package.Entry);
            var stopped = new JournalWriterStore(_database).ObserveForCurrentExecutor(_installed.JournalId);
            if (!stopped.IsComplete || stopped.State != WriterState.Stopped)
                throw new IOException("extension_original_writer_stop_unconfirmed");
            using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
            using var transaction = connection.BeginTransaction(deferred: true);
            var saved = RecoveryJournalRegistry.ReadValidatedSnapshotInside(connection, transaction);
            var settings = HostSettingsRows.ReadCurrent(connection, transaction);
            if (settings.Revision != _settingsRevision || !saved.IsReady || saved.Isolations.Any(item => Conflicts(item.ResourceKey, item.Mode)) ||
                ExtensionInstalledRows.ReadCurrentMetadata(connection, transaction, _installed.ExtensionId) != _installed ||
                settings.Values.Extensions
                    .SingleOrDefault(item => item.ExtensionId == _installed.ExtensionId)?.Enabled != true) throw Invalid();
            transaction.Commit();
            return bound;
        }));
    }

    internal IDisposable? ProtectPaths()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _evidence?.ProtectPaths();
    }

    internal IDisposable HoldRuntimeFiles(out byte[] manifestBytes)
    {
        using var paths = _evidence?.ProtectPaths();
        var bound = RequireCurrent();
        var held = TreeReadLease.Acquire(bound.Footprint!.TargetPath, bound.Footprint.ParentIdentity, bound.Footprint.Payload);
        try
        {
            var manifest = bound.Footprint.Payload.Snapshot.Entries.Single(item => item.RelativePath == "manifest.json");
            manifestBytes = File.ReadAllBytes(Path.Combine(bound.Footprint.TargetPath, "manifest.json"));
            if (manifestBytes.LongLength != manifest.SizeBytes ||
                Convert.ToHexStringLower(SHA256.HashData(manifestBytes)) != manifest.Sha256) throw Invalid();
            _ = new ExtensionManifestAdmission(_contracts).ReadCurrent(manifestBytes, _installed.ExtensionId);
            return held;
        }
        catch { held.Dispose(); throw; }
    }

    private bool Conflicts(string key, ResourceClaimMode mode) => _claims.Any(claim =>
        claim.ResourceKey == key && (mode == ResourceClaimMode.Mutation || claim.Mode == ResourceClaimMode.Mutation));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _evidence?.Dispose(); }
        finally { _borrow.Dispose(); }
    }

    private static IOException Invalid() => new("extension_installed_activation_unbound");
}
