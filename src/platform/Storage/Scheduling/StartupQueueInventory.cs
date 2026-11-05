using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Scheduling;

internal static class StartupQueueInventory
{
    internal static void Verify(RuntimeInstance instance, StateDatabase database)
    {
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot) throw StartupQueueRows.Invalid();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        var after = "";
        while (true)
        {
            command.Parameters.Clear(); command.CommandText = "SELECT generation FROM startup_queue_batches WHERE generation>$after ORDER BY generation LIMIT 128;";
            command.Parameters.AddWithValue("$after", after); var page = new List<string>();
            using (var reader = command.ExecuteReader()) while (reader.Read()) page.Add(reader.GetString(0));
            if (page.Count == 0) break;
            foreach (var id in page)
            {
                var batch = StartupQueueRows.ReadBatch(connection, transaction, StartupQueueRows.ParseId(id), instance.InstanceId) ?? throw StartupQueueRows.Invalid();
                foreach (var occurrence in batch.Occurrences)
                    _ = StartupQueueRows.Read(connection, transaction, occurrence.OccurrenceId, instance.InstanceId, batch);
            }
            after = page[^1];
        }
        command.Parameters.Clear();
        command.CommandText = "SELECT 1 FROM operations o LEFT JOIN startup_queue_batches b ON b.operation_id=o.operation_id WHERE o.operation_kind='schedule.startup' AND b.operation_id IS NULL LIMIT 1;";
        if (command.ExecuteScalar() is not null) throw StartupQueueRows.Invalid();
        command.CommandText = "SELECT 1 FROM startup_queue_outcomes o LEFT JOIN startup_queue_occurrences c ON c.occurrence_id=o.occurrence_id WHERE c.occurrence_id IS NULL OR o.reason_code NOT IN ('startup_owner_restarted','schedule_definition_changed','schedule_definition_deleted','schedule_settings_changed','schedule_inputs_unavailable','schedule_budget_continuity_unknown') LIMIT 1;";
        if (command.ExecuteScalar() is not null) throw StartupQueueRows.Invalid();
        command.CommandText = "SELECT 1 FROM startup_queue_occurrences c LEFT JOIN startup_queue_batches b ON b.generation=c.generation WHERE b.generation IS NULL LIMIT 1;";
        if (command.ExecuteScalar() is not null) throw StartupQueueRows.Invalid();
    }
}
