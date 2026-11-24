using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Scheduling;

internal static class ScheduledProviderInventory
{
    internal static void Visit(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        Action<ScheduledProviderState> visit)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var transaction = DatabaseConnections.BeginRead(connection);
        Visit(instance, contracts, connection, transaction, visit);
        transaction.Commit();
    }

    internal static void Visit(RuntimeInstance instance, ContractReader contracts, SqliteConnection connection,
        SqliteTransaction transaction, Action<ScheduledProviderState> visit)
    {
        var after = "";
        while (true)
        {
            using var command = DatabaseConnections.Command(connection, transaction,
                "SELECT occurrence_id,relative_path,size_bytes,sha256 FROM main.scheduled_provider_occurrences WHERE occurrence_id>$0 ORDER BY occurrence_id LIMIT 200;", after);
            var page = new List<(string Id, BlobReference Reference)>();
            using (var rows = command.ExecuteReader())
                while (rows.Read()) page.Add((rows.GetString(0), new(rows.GetString(1), rows.GetInt64(2), rows.GetString(3))));
            if (page.Count == 0) break;
            foreach (var item in page)
            {
                var saved = new ScheduledProviderEvidence(instance, contracts).Read(item.Reference, connection, transaction);
                if (saved.Source.Document.OperationId.ToString("D") != item.Id) throw ScheduledProviderCodec.Invalid();
                visit(saved);
            }
            after = page[^1].Id;
        }
        using var orphans = DatabaseConnections.Command(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM main.operations o LEFT JOIN main.scheduled_provider_occurrences s ON s.operation_id=o.operation_id WHERE o.operation_kind='schedule.provider-invoke' AND s.operation_id IS NULL) " +
            "OR EXISTS(SELECT 1 FROM main.scheduled_provider_begin b LEFT JOIN main.scheduled_provider_occurrences s ON s.occurrence_id=b.occurrence_id WHERE s.occurrence_id IS NULL) " +
            "OR EXISTS(SELECT 1 FROM main.scheduled_provider_close b LEFT JOIN main.scheduled_provider_occurrences s ON s.occurrence_id=b.occurrence_id WHERE s.occurrence_id IS NULL) " +
            "OR EXISTS(SELECT 1 FROM main.scheduled_provider_result b LEFT JOIN main.scheduled_provider_occurrences s ON s.occurrence_id=b.occurrence_id WHERE s.occurrence_id IS NULL) " +
            "OR EXISTS(SELECT 1 FROM main.scheduled_provider_pending b LEFT JOIN main.scheduled_provider_occurrences s ON s.occurrence_id=b.occurrence_id WHERE s.occurrence_id IS NULL);");
        if ((long)orphans.ExecuteScalar()! != 0) throw ScheduledProviderCodec.Invalid();
        ScheduleWaitingCapacity.VerifyInventory(connection, transaction);
    }

    internal static IReadOnlyList<ScheduledProviderState> ReadPending(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var transaction = DatabaseConnections.BeginRead(connection);
        ScheduleWaitingCapacity.VerifyInventory(connection, transaction);
        using var command = DatabaseConnections.Command(connection, transaction,
            "SELECT p.occurrence_id,s.relative_path,s.size_bytes,s.sha256 FROM main.scheduled_provider_pending p" +
            " LEFT JOIN main.scheduled_provider_occurrences s ON s.occurrence_id=p.occurrence_id ORDER BY p.occurrence_id;");
        var references = new List<(string Id, BlobReference Reference)>();
        using (var rows = command.ExecuteReader())
            while (rows.Read())
            {
                if (rows.IsDBNull(1)) throw ScheduledProviderCodec.Invalid();
                references.Add((rows.GetString(0), new(rows.GetString(1), rows.GetInt64(2), rows.GetString(3))));
            }
        var evidence = new ScheduledProviderEvidence(instance, contracts);
        var pending = new List<ScheduledProviderState>(references.Count);
        foreach (var (id, reference) in references)
        {
            var state = evidence.Read(reference, connection, transaction);
            if (state.Result is not null || state.Source.Document.OperationId.ToString("D") != id) throw ScheduledProviderCodec.Invalid();
            pending.Add(state);
        }
        transaction.Commit();
        return pending;
    }

}
