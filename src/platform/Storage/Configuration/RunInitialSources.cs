using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

internal static class RunInitialSources
{
    internal static IReadOnlyDictionary<ConfigurationAddress, StoredConfigurationRevision> Read(RuntimeInstance instance,
        StateDatabase database, ContractReader contracts, IReadOnlyDictionary<ConfigurationAddress, FrozenRunState> states)
    {
        var snapshots = new ConfigurationSnapshotCodec(new(instance, contracts));
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        return states.ToDictionary(item => item.Key, item => ConfigurationRevisionRows.Read(connection, transaction,
            item.Key, item.Value.InitialPublicationRevision, snapshots));
    }

    internal static void RequireInside(SqliteConnection connection, SqliteTransaction transaction,
        FrozenRunState state, StoredConfigurationRevision publication)
    {
        if (state.InitialPublicationRevision != publication.Revision.Revision) throw new IOException("configuration_run_publication_cursor_unbound");
        ConfigurationRevisionRows.RequireHistoricalFrozen(connection, transaction, state.Source, state.BindingRevision,
            state.SnapshotReference ?? throw new IOException("configuration_run_source_snapshot_unbound"), publication);
    }
}
