using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

internal static class ConfigurationRunHistory
{
    private static readonly HashSet<string> Reasons = ["configuration_sync_allowed", "configuration_sync_disabled", "configuration_sync_not_due",
        "configuration_sync_empty", "configuration_sync_shape_conflict", "configuration_sync_shrinking", "configuration_sync_invalid_content",
        "configuration_sync_capture_rejected", "configuration_sync_unchanged"];

    internal static void Verify(SqliteConnection connection, SqliteTransaction transaction, ConfigurationRunStatus session, string subject,
        IReadOnlyDictionary<ConfigurationAddress, FrozenRunState> sources, ConfigurationSnapshotCodec snapshots)
    {
        var operations = new List<Guid>(); long lastElapsed = 0;
        using (var command = ConfigurationRunRows.Command(connection, transaction, session.SessionId))
        {
            command.CommandText = "SELECT ordinal,operation_id,elapsed_milliseconds FROM configuration_run_syncs WHERE session_id=$session ORDER BY ordinal LIMIT 10001;";
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                var finish = session.CompletionOperationId?.ToString("D") == rows.GetString(1);
                if (operations.Count >= 2 || rows.GetInt64(0) != operations.Count + 1 || rows.GetInt64(2) < Math.Max(finish ? 0 : 15000, lastElapsed) ||
                    !finish && operations.Count != 0 ||
                    rows.GetInt64(2) > 922337203685477 || session.CompletionOperationId?.ToString("D") == rows.GetString(1) &&
                    session.ClosedElapsedMilliseconds != rows.GetInt64(2))
                    throw new IOException("configuration_run_sync_order_unbound");
                operations.Add(Guid.Parse(rows.GetString(1))); lastElapsed = rows.GetInt64(2);
            }
        }
        var executed = RunStopEvidence.HasExecutedWriter(connection, transaction, session);
        var terminal = session.CompletionOperationId.HasValue;
        var completedWithSample = terminal && operations.Contains(session.CompletionOperationId!.Value);
        if (session.ClosedElapsedMilliseconds.HasValue && session.ClosedElapsedMilliseconds < lastElapsed ||
            terminal && completedWithSample != executed ||
            operations.Count != session.SyncOrdinal || session.Revision != 1 + (session.EnteredRunning ? 1 : 0) + operations.Count + (terminal && !completedWithSample ? 1 : 0) ||
            !session.EnteredRunning && (session.Phase == ConfigurationRunPhase.Running || session.SyncOrdinal != 0) ||
            session.Phase == ConfigurationRunPhase.Prepared && (session.EnteredRunning || terminal) ||
            completedWithSample && operations[^1] != session.CompletionOperationId)
            throw new IOException("configuration_run_sync_cursor_unbound");
        var revisions = session.Resources.ToDictionary(item => item.Address, item => item.ExpectedRevision);
        if (terminal && !completedWithSample) operations.Add(session.CompletionOperationId!.Value);
        foreach (var operation in operations)
            VerifyOperation(connection, transaction, session, subject, operation, operation == session.CompletionOperationId, executed, revisions, sources, snapshots);
        if (session.Resources.Any(item => revisions[item.Address] != item.CurrentRevision)) throw new IOException("configuration_run_resource_cursor_unbound");
    }

    private static void VerifyOperation(SqliteConnection connection, SqliteTransaction transaction, ConfigurationRunStatus session, string subject,
        Guid operationId, bool finish, bool executed, IDictionary<ConfigurationAddress, long> revisions,
        IReadOnlyDictionary<ConfigurationAddress, FrozenRunState> sources, ConfigurationSnapshotCodec snapshots)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT subject_id,operation_kind,status,request_digest FROM operations WHERE operation_id=$operation;";
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        using (var row = command.ExecuteReader())
            if (!row.Read() || row.GetString(0) != subject || row.GetString(1) != (finish ? "configuration.run.finish" : "configuration.run.sync") ||
                row.GetString(2) != "completed" || !FileTreePolicy.Digest(row.GetString(3))) throw new IOException("configuration_run_sync_operation_unbound");
        command.CommandText = "SELECT resource_id,previous_revision,current_revision,reason_code FROM configuration_run_sync_items WHERE operation_id=$operation ORDER BY resource_id LIMIT 33;";
        var items = new List<(ConfigurationAddress Address, long Previous, long Current, string Reason)>();
        using (var rows = command.ExecuteReader())
            while (rows.Read()) items.Add((new(session.Resources[0].Address.BindingId, rows.GetString(0)), rows.GetInt64(1), rows.GetInt64(2), rows.GetString(3)));
        if (!items.OrderBy(item => item.Address.ResourceId, StringComparer.Ordinal).Select(item => item.Address).SequenceEqual(session.Resources.Select(item => item.Address)))
            throw new IOException("configuration_run_sync_membership_unbound");
        foreach (var item in items)
        {
            if (!Reasons.Contains(item.Reason) || item.Previous != revisions[item.Address] ||
                item.Current != item.Previous + (item.Reason == "configuration_sync_allowed" ? 1 : 0) ||
                !(sources[item.Address].AutoSyncEnabled && (finish ? sources[item.Address].SyncAtFinish : sources[item.Address].SyncInitially)) !=
                    (item.Reason == "configuration_sync_disabled") ||
                item.Reason == "configuration_sync_not_due" && (!finish || executed) ||
                finish && !executed && item.Reason is not ("configuration_sync_not_due" or "configuration_sync_disabled"))
                throw new IOException("configuration_run_sync_result_unbound");
            revisions[item.Address] = item.Current;
            foreach (var revision in new[] { item.Previous, item.Current }.Distinct())
            {
                var saved = ConfigurationRevisionRows.Read(connection, transaction, item.Address, revision, snapshots).Revision;
                var source = sources[item.Address].Source;
                if (saved.UserId != source.UserId || saved.ScriptId != source.ScriptId || saved.Contract != source.Contract)
                    throw new IOException("configuration_run_history_revision_unbound");
            }
        }
        command.CommandText = "SELECT binding_id,resource_id,revision,has_changes FROM configuration_batch_items WHERE operation_id=$operation;";
        var published = new List<(ConfigurationAddress Address, long Revision)>();
        using (var rows = command.ExecuteReader())
            while (rows.Read())
            {
                if (!rows.GetBoolean(3)) throw new IOException("configuration_run_sync_publication_unbound");
                published.Add((new(Guid.Parse(rows.GetString(0)), rows.GetString(1)), rows.GetInt64(2)));
            }
        if (!published.OrderBy(item => item.Address.ResourceId, StringComparer.Ordinal).SequenceEqual(items.Where(item => item.Reason == "configuration_sync_allowed")
            .OrderBy(item => item.Address.ResourceId, StringComparer.Ordinal).Select(item => (item.Address, item.Current))))
            throw new IOException("configuration_run_sync_publication_unbound");
    }
}
