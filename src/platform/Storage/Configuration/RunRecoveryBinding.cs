using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

internal static class RunRecoveryBinding
{
    internal static ConfigurationRunStatus? Read(RuntimeInstance instance, StateDatabase database, ContractReader contracts, Guid journalId)
    {
        Guid sessionId; string subject;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT s.session_id,o.subject_id FROM configuration_run_resources r " +
                "JOIN configuration_run_sessions s ON s.session_id=r.session_id JOIN operations o ON o.operation_id=s.session_id " +
                "WHERE r.journal_id=$journal AND o.operation_kind='configuration.run.begin';";
            command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            using var row = command.ExecuteReader();
            if (!row.Read()) return null;
            sessionId = Guid.Parse(row.GetString(0)); subject = row.GetString(1);
            if (row.Read()) throw new IOException("configuration_run_membership_unbound");
        }
        var session = new ConfigurationRunStore(instance, database, contracts).Read(sessionId, subject)
            ?? throw new IOException("configuration_run_missing");
        _ = ConfigurationRunBinding.Read(instance, database, contracts, session);
        if (!session.Resources.Any(item => item.JournalId == journalId)) throw new IOException("configuration_run_membership_unbound");
        return session;
    }
}
