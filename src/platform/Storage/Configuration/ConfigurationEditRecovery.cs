using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

public static class ConfigurationEditRecovery
{
    public static void ReconcileAndPublish(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, RecoveryIndex index, PayloadApplyRoots? payloadRoots = null)
    {
        lease.VerifyHeld(instance);
        try
        {
            var store = new ConfigurationEditStore(instance, database, contracts);
            var journals = new RecoveryJournalRegistry(database);
            foreach (var (sessionId, subject) in OpenSessions(database))
            {
                var session = store.Read(sessionId, subject)!;
                if (session.Phase != ConfigurationEditPhase.Prepared || session.Resources.Any(resource =>
                    journals.FindJournal(resource.JournalId) is not { Phase: JournalPhase.Committed })) continue;
                var cancel = new ConfigurationEditCancel(sessionId, session.Revision);
                store.Cancel(ConfigurationEditStore.CancelIdentity(Guid.NewGuid(), subject, cancel), cancel, DateTimeOffset.UtcNow);
            }
            PublishHolds(database, index, payloadRoots);
        }
        catch { index.BlockUnknown("configuration_edit_reconciliation_required"); throw; }
    }

    public static void PublishHolds(StateDatabase database, RecoveryIndex index, PayloadApplyRoots? payloadRoots = null) =>
        ConfigurationRecoveryHolds.Publish(database, index, payloadRoots);

    private static IReadOnlyList<(Guid Id, string Subject)> OpenSessions(StateDatabase database)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT s.session_id,o.subject_id FROM configuration_edit_sessions s JOIN operations o ON o.operation_id=s.session_id WHERE s.phase IN (0,1) LIMIT 4097;";
        var sessions = new List<(Guid, string)>();
        using var row = command.ExecuteReader();
        while (row.Read()) sessions.Add((Guid.Parse(row.GetString(0)), row.GetString(1)));
        if (sessions.Count > 4096) throw new IOException("configuration_edit_inventory_limit");
        return sessions;
    }
}
