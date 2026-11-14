using Microsoft.Data.Sqlite;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class DatabaseRelations
{
    private sealed record Relation(string Child, string Parent, string[] Columns, string[] Targets);
    private static readonly Relation[] Relations =
    [
        new("automation_mutations", "operations", ["operation_id"], ["operation_id"]),
        new("automation_imports", "operation_journals", ["journal_id"], ["journal_id"]),
        new("automation_imports", "operations", ["operation_id"], ["operation_id"]),
        new("automation_assets", "blobs", ["digest"], ["digest"]),
        new("settings_mutations", "operations", ["operation_id"], ["operation_id"]),
        new("secret_preparations", "operation_journals", ["journal_id"], ["journal_id"]),
        new("secret_preparations", "operations", ["operation_id"], ["operation_id"]),
        new("secret_retirements", "operation_journals", ["journal_id"], ["journal_id"]),
        new("secret_retirements", "operations", ["operation_id"], ["operation_id"]),
        new("secret_mutations", "operations", ["operation_id"], ["operation_id"]),
        new("attempt_receipts", "operations", ["operation_id"], ["operation_id"]),
        new("attempt_receipts", "operation_journals", ["journal_id"], ["journal_id"]),
        new("record_assets", "blobs", ["digest"], ["digest"]),
        new("configuration_current", "bindings", ["binding_id"], ["binding_id"]),
        new("configuration_revision_assets", "blobs", ["digest"], ["digest"]),
        new("configuration_mutations", "operations", ["operation_id"], ["operation_id"]),
        new("configuration_batch_results", "operations", ["operation_id"], ["operation_id"]),
        new("configuration_batch_items", "operations", ["operation_id"], ["operation_id"]),
        new("configuration_edit_sessions", "operations", ["completion_operation_id"], ["operation_id"]),
        new("configuration_edit_sessions", "operations", ["session_id"], ["operation_id"]),
        new("configuration_edit_resources", "operation_journals", ["journal_id"], ["journal_id"]),
        new("configuration_run_sessions", "operations", ["completion_operation_id"], ["operation_id"]),
        new("configuration_run_sessions", "operation_journals", ["parent_journal_id"], ["journal_id"]),
        new("configuration_run_sessions", "operations", ["owner_operation_id"], ["operation_id"]),
        new("configuration_run_sessions", "operations", ["session_id"], ["operation_id"]),
        new("configuration_run_resources", "operation_journals", ["journal_id"], ["journal_id"]),
        new("configuration_run_resources", "operations", ["operation_id"], ["operation_id"]),
        new("configuration_run_syncs", "operations", ["operation_id"], ["operation_id"]),
        new("configuration_run_sync_items", "operations", ["operation_id"], ["operation_id"]),
        new("execution_retry_intents", "attempt_receipts", ["previous_attempt_id"], ["attempt_id"]),
        new("completion_candidates", "attempt_receipts", ["attempt_id"], ["attempt_id"]),
        new("notification_task_ids", "operations", ["first_operation_id"], ["operation_id"]),
        new("notification_publications", "operations", ["operation_id"], ["operation_id"]),
        new("notification_publication_fields", "operations", ["child_operation_id"], ["operation_id"]),
        new("notification_deliveries", "attempt_receipts", ["source_attempt_id"], ["attempt_id"]),
        new("notification_deliveries", "operations", ["operation_id"], ["operation_id"]),
        new("notification_delivery_secrets", "secret_payloads", ["secret_id"], ["secret_id"]),
        new("notification_receipt_sources", "attempt_receipts", ["attempt_id"], ["attempt_id"]),
        new("notification_receipt_sources", "run_records", ["run_id"], ["run_id"]),
        new("notification_receipt_sources", "operations", ["operation_id"], ["operation_id"]),
        new("notification_receipt_secrets", "secret_payloads", ["secret_id"], ["secret_id"]),
        new("notification_delivery_pending", "operations", ["source_operation_id"], ["operation_id"]),
        new("notification_saved_tests", "operations", ["operation_id"], ["operation_id"]),
        new("notification_scheduled_sources", "scheduled_provider_occurrences", ["occurrence_id"], ["occurrence_id"]),
        new("notification_scheduled_sources", "operations", ["operation_id"], ["operation_id"]),
        new("execution_log_catalog", "operation_journals", ["journal_id"], ["journal_id"]),
        new("record_content_retirements", "operation_journals", ["cleanup_journal_id"], ["journal_id"]),
        new("record_content_retirements", "operations", ["operation_id"], ["operation_id"]),
        new("execution_image_catalog", "operation_journals", ["journal_id"], ["journal_id"]),
        new("extension_catalog_acceptances", "operations", ["first_operation_id"], ["operation_id"]),
        new("extension_installation_revisions", "operation_journals", ["journal_id"], ["journal_id"]),
        new("trusted_update_feeds", "operations", ["first_operation_id"], ["operation_id"]),
        new("maintenance_operations", "operations", ["operation_id"], ["operation_id"]),
        new("extension_documents", "operations", ["operation_id"], ["operation_id"]),
        new("extension_document_commits", "operations", ["operation_id"], ["operation_id"]),
        new("secret_document_publications", "operations", ["operation_id"], ["operation_id"]),
        new("work_effect_rotations", "secret_document_publications", ["operation_id"], ["operation_id"]),
        new("notification_manual_sources", "manual_work_runs", ["invocation_operation_id"], ["operation_id"]),
        new("notification_manual_sources", "operations", ["operation_id"], ["operation_id"]),
    ];
    private static readonly IReadOnlyDictionary<string, Relation[]> Children = Relations.GroupBy(item => item.Child)
        .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> Parents = Relations.Select(item => item.Parent).ToHashSet(StringComparer.Ordinal);

    internal static bool TracksChild(string table) => Children.ContainsKey(table);
    internal static bool TracksParent(string table) => Parents.Contains(table);

    private static string Missing(Relation relation) =>
        "(" + string.Join(" AND ", relation.Columns.Select(column => "c." + column + " IS NOT NULL")) +
        " AND NOT EXISTS(SELECT 1 FROM " + DatabaseTables.Name(relation.Parent) + " p WHERE " +
        string.Join(" AND ", relation.Columns.Zip(relation.Targets, (column, target) => "p." + target + "=c." + column)) + "))";

    internal static void VerifyAll(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        foreach (var (table, relations) in Children) Require(connection, transaction, table, relations, null);
    }

    internal static void VerifyChanges(SqliteConnection connection, SqliteTransaction transaction, DatabaseChanges changes)
    {
        foreach (var (table, relations) in Children)
        {
            if (relations.Any(relation => changes.DeletedParents.Contains(relation.Parent)))
                Require(connection, transaction, table, relations, null);
            else if (changes.Rows.TryGetValue(table, out var rows))
                foreach (var batch in rows.Chunk(256)) Require(connection, transaction, table, relations, batch);
        }
    }

    private static void Require(SqliteConnection connection, SqliteTransaction? transaction, string table, Relation[] relations, long[]? rows)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        var selection = rows is null ? "" : "c.rowid IN (" + string.Join(",", rows.Select((_, index) => "$" + index)) + ") AND ";
        command.CommandText = "SELECT 1 FROM " + DatabaseTables.Name(table) + " c WHERE " + selection +
            "(" + string.Join(" OR ", relations.Select(Missing)) + ") LIMIT 1;";
        if (rows is not null)
            for (var index = 0; index < rows.Length; index++) command.Parameters.AddWithValue("$" + index, rows[index]);
        if (command.ExecuteScalar() is not null) throw new IOException("database_relation_unbound_" + table);
    }

    internal static void Configure(SqliteConnection connection)
    {
        foreach (var group in Relations.GroupBy(item => item.Parent))
        {
            var domain = DatabaseTables.Domain(group.Key);
            if ((DatabaseConnections.Domains(connection) & domain) == 0) continue;
            var columns = group.SelectMany(item => item.Targets).Distinct().ToArray();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TEMP TRIGGER IF NOT EXISTS key_" + group.Key + " BEFORE UPDATE OF " +
                string.Join(",", columns) + " ON " + DatabaseTables.Name(group.Key) + " WHEN " +
                string.Join(" OR ", columns.Select(column => "OLD." + column + " IS NOT NEW." + column)) +
                " BEGIN SELECT RAISE(ABORT,'database_key_immutable'); END;";
            command.ExecuteNonQuery();
        }
    }
}
