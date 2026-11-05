using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record BoundExtensionInstallation(ExtensionInstalledMetadata Metadata,
    ExtensionInstalledFootprint Footprint, BoundExtensionPayload Operation);

internal sealed class ExtensionInstalledEvidence(RuntimeInstance instance, StateDatabase database,
    ContractReader contracts, PayloadApplyRoots roots)
{
    internal BoundExtensionInstallation? ReadCurrent(string extensionId)
    {
        ExtensionInstalledMetadata? saved;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            saved = ExtensionInstalledRows.ReadCurrentMetadata(connection, transaction, extensionId);
            transaction.Commit();
        }
        if (saved is null) return null;
        var bound = new ExtensionPayloadBinding(instance, database, contracts, roots).Read(saved.JournalId);
        if (bound.Registered.Phase != NexusPipeline.Core.Configuration.JournalPhase.Committed ||
            bound.Commit is null || bound.Footprint is null || bound.Commit.Footprint != saved.Footprint ||
            ExtensionPayloadBinding.DerivedInstallation(bound.Plan, bound.Commit, bound.Footprint) != saved)
            throw Invalid();
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            if (ExtensionInstalledRows.ReadCurrentMetadata(connection, transaction, extensionId) != saved) throw Invalid();
            transaction.Commit();
        }
        return new(saved, bound.Footprint, bound);
    }

    internal InstallationReadLease? ProtectCurrent(string extensionId, InstanceLease lease)
    {
        ExtensionInstalledMetadata? saved;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            saved = ExtensionInstalledRows.ReadCurrentMetadata(connection, transaction, extensionId);
            transaction.Commit();
        }
        if (saved is not { IsInstalled: true }) throw Invalid();
        return InstallationReadLease.TryAcquire(instance, database, lease, contracts, roots, saved.JournalId,
            () => ReadCurrent(extensionId)?.Operation ?? throw Invalid());
    }

    private static IOException Invalid() => new("extension_installed_evidence_changed");
}
