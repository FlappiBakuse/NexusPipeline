using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class InstalledConsumptionFacts(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
    ContractReader contracts, PayloadApplyRoots roots, BlobReference plan, BlobReference source, BlobReference physical,
    Guid journal, Guid activation, string extension, bool existingInstallation) : IAsyncDisposable
{
    private readonly object _gate = new();
    private InstallationReadLease? _evidence;
    private bool _disposed;

    internal void PublishCurrent(ResourceLeaseManager resources, RecoveryIndex recovery) =>
        ExtensionProjectionPublication.PublishIfDirty(instance, database, lease, contracts, roots, resources, recovery);

    internal InstallationReadLease? OpenReadLease()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return InstallationReadLease.TryAcquire(instance, database, lease, contracts, roots, journal, ReadCurrent);
        }
    }

    internal InstallationReadLease? BorrowReadLease()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _evidence ??= InstallationReadLease.TryAcquire(instance, database, lease, contracts, roots, journal, ReadCurrent);
        }
    }

    internal void RequireCurrent()
    {
        lock (_gate)
        {
            var held = BorrowReadLease();
            if (held is null) _ = ReadCurrent();
            else held.RequireCurrent();
        }
    }

    private BoundExtensionPayload ReadCurrent()
    {
        lease.VerifyHeld(instance);
        var bound = new ExtensionPayloadBinding(instance, database, contracts, roots).Read(journal, plan);
        if (bound.Registered.Phase != JournalPhase.Committed || bound.Commit is not { } commit ||
            bound.Footprint is not { IsInstalled: true } footprint || commit.Source != source ||
            commit.PhysicalResult != physical || !existingInstallation &&
            (commit.ActivationId != activation || commit.ActivationState != "published" || !commit.DesiredEnabled)) throw Unavailable();
        var expected = ExtensionPayloadBinding.DerivedInstallation(bound.Plan, commit, footprint);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        if (ExtensionInstalledRows.ReadCurrentMetadata(connection, transaction, extension) != expected ||
            HostSettingsRows.ReadCurrent(connection, transaction).Values.Extensions
                .SingleOrDefault(item => item.ExtensionId == extension)?.Enabled != true) throw Unavailable();
        transaction.Commit();
        return bound;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true; _evidence?.Dispose(); _evidence = null;
            return ValueTask.CompletedTask;
        }
    }

    private static IOException Unavailable() => new("extension_installation_consumption_unavailable");
}
