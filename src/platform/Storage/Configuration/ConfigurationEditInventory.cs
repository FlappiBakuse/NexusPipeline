using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

public static class ConfigurationEditInventory
{
    public static void VerifyReadOnly(StateDatabase database)
    {
        try
        {
            using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Automation | DatabaseDomain.Configuration);
            using var transaction = connection.BeginTransaction(deferred: true);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT s.session_id,o.subject_id,o.operation_kind,o.status FROM configuration.configuration_edit_sessions s " +
                "JOIN main.operations o ON o.operation_id=s.session_id LIMIT 4097;";
            var sessions = new List<(Guid Id, string Subject, string Kind, string Status)>();
            using (var row = command.ExecuteReader()) while (row.Read()) sessions.Add((Guid.Parse(row.GetString(0)), row.GetString(1), row.GetString(2), row.GetString(3)));
            if (sessions.Count > 4096) throw new IOException("configuration_edit_inventory_limit");
            foreach (var item in sessions)
            {
                var session = ConfigurationEditRows.Read(connection, transaction, item.Id, item.Subject)!;
                if (item.Kind != "configuration.edit.begin" || session.Phase != ConfigurationEditPhase.Prepared && item.Status != "completed")
                    throw new IOException("configuration_edit_operation_unbound");
                foreach (var resource in session.Resources) VerifyResource(connection, transaction, session, resource);
                if (session.CompletionOperationId is { } completion) VerifyCompletion(connection, transaction, session, completion, item.Subject);
                else _ = ConfigurationRevisionRows.Binding(connection, transaction, session.Resources[0].Address.BindingId);
            }
        }
        catch (Exception error) when (error is ConfigurationException or FormatException or InvalidOperationException or ArgumentException)
        { throw new IOException("configuration_edit_metadata_unrecognized", error); }
    }

    private static void VerifyResource(SqliteConnection connection, SqliteTransaction transaction,
        ConfigurationEditStatus session, ConfigurationEditResource resource)
    {
        if (!FileTreePolicy.Digest(resource.PlanDigest) || resource.PlanPath != $"blobs/plans/{resource.PlanDigest}.json")
            throw new IOException("configuration_edit_plan_unrecognized");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT operation_id,operation_kind,phase,plan_path,plan_digest FROM main.operation_journals WHERE journal_id=$journal " +
            "AND EXISTS(SELECT 1 FROM main.journal_resources WHERE journal_id=$journal AND resource_key=$resource);";
        command.Parameters.AddWithValue("$journal", resource.JournalId.ToString("D"));
        command.Parameters.AddWithValue("$resource", ConfigurationRevisionService.ResourceKey(resource.Address));
        using (var row = command.ExecuteReader())
            if (!row.Read() || row.GetString(0) != session.SessionId.ToString("D") || row.GetString(1) != "configEdit" ||
                row.GetString(3) != resource.PlanPath || row.GetString(4) != resource.PlanDigest ||
                session.CompletionOperationId.HasValue && row.GetString(2) != "committed") throw new IOException("configuration_edit_journal_unbound");
        if (resource.ExpectedRevision == 0) return;
        command.CommandText = "SELECT contract_revision,contract_digest,instance_key,requires_instance FROM configuration.configuration_revisions " +
            "WHERE binding_id=$binding AND resource_id=$id AND revision=$revision;";
        command.Parameters.AddWithValue("$binding", resource.Address.BindingId.ToString("D"));
        command.Parameters.AddWithValue("$id", resource.Address.ResourceId); command.Parameters.AddWithValue("$revision", resource.ExpectedRevision);
        using var baseline = command.ExecuteReader();
        if (!baseline.Read() || new ConfigurationContractStamp(baseline.GetInt64(0), baseline.GetString(1), baseline.IsDBNull(2) ? null : baseline.GetString(2),
            baseline.GetBoolean(3)) != resource.Contract) throw new IOException("configuration_edit_baseline_unbound");
    }

    private static void VerifyCompletion(SqliteConnection connection, SqliteTransaction transaction,
        ConfigurationEditStatus session, Guid operationId, string subject)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT subject_id,operation_kind,status FROM main.operations WHERE operation_id=$operation;";
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        using (var row = command.ExecuteReader())
            if (!row.Read() || row.GetString(0) != subject || row.GetString(2) != "completed" ||
                row.GetString(1) != (session.Phase == ConfigurationEditPhase.Saved ? "configuration.edit.save" : "configuration.edit.cancel"))
                throw new IOException("configuration_edit_completion_unbound");
        command.CommandText = "SELECT binding_id,resource_id,revision FROM configuration.configuration_batch_items WHERE operation_id=$operation;";
        var actual = new List<(Guid Binding, string Resource, long Revision)>();
        using (var row = command.ExecuteReader()) while (row.Read()) actual.Add((Guid.Parse(row.GetString(0)), row.GetString(1), row.GetInt64(2)));
        var expected = session.Phase == ConfigurationEditPhase.Saved ? session.Resources.Select(resource =>
            (resource.Address.BindingId, resource.Address.ResourceId, resource.ExpectedRevision + 1)).ToArray() : [];
        if (!actual.OrderBy(item => item.Resource, StringComparer.Ordinal).SequenceEqual(expected))
            throw new IOException("configuration_edit_result_unrecognized");
    }
}
