using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class RecoveryCapacityStore
{
    internal const string ActiveOwners = "SELECT owner_journal_id FROM main.recovery_capacity_reservations INDEXED BY recovery_capacity_open_read WHERE released=0 " +
        "UNION SELECT cm.owner_journal_id FROM main.file_effects f INDEXED BY file_effects_pending_read " +
        "JOIN main.recovery_capacity_members cm USING(journal_id) WHERE f.state='pending'";
    internal const string ActiveReservation = "(r.owner_journal_id IN (" + ActiveOwners + "))";
    private const string Usage = "SELECT COALESCE(sum((SELECT count(*) FROM main.recovery_capacity_members m WHERE m.owner_journal_id=r.owner_journal_id)+remaining_journals),0)," +
        "COALESCE(sum((SELECT count(*) FROM main.file_effects f JOIN main.recovery_capacity_members m USING(journal_id) WHERE m.owner_journal_id=r.owner_journal_id)+remaining_effects),0)," +
        "COALESCE(sum((SELECT count(*) FROM main.journal_writer_jobs j JOIN main.recovery_capacity_members m USING(journal_id) WHERE m.owner_journal_id=r.owner_journal_id)+remaining_jobs),0) " +
        "FROM main.recovery_capacity_reservations r WHERE " + ActiveReservation + ";";
    internal static void Register(SqliteConnection connection, SqliteTransaction transaction, Guid journalId,
        RecoveryCapacityBudget budget, Guid? parentJournalId)
    {
        budget.RequireValid();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        if (parentJournalId is { } parent)
        {
            command.Parameters.AddWithValue("$parent", parent.ToString("D"));
            command.CommandText = "INSERT INTO main.recovery_capacity_members SELECT $journal,m.owner_journal_id FROM main.recovery_capacity_members m " +
                "JOIN main.operation_journals j ON j.journal_id=m.journal_id WHERE m.journal_id=$parent AND j.operation_kind='runAttempt' " +
                "AND j.phase IN ('prepared','applying');";
            if (command.ExecuteNonQuery() != 1) throw new IOException("recovery_capacity_parent_unbound");
            return;
        }
        command.CommandText = Usage;
        using (var row = command.ExecuteReader())
        {
            if (!row.Read() || row.GetInt64(0) + budget.Journals > RecoveryCapacityBudget.MaximumJournals ||
                row.GetInt64(1) + budget.FileEffects > RecoveryCapacityBudget.MaximumFileEffects ||
                row.GetInt64(2) + budget.Jobs > RecoveryCapacityBudget.MaximumJobs)
                throw new IOException("recovery_capacity_exceeded");
        }
        command.Parameters.AddWithValue("$journals", budget.Journals);
        command.Parameters.AddWithValue("$effects", budget.FileEffects);
        command.Parameters.AddWithValue("$jobs", budget.Jobs);
        command.CommandText = "INSERT INTO main.recovery_capacity_reservations VALUES($journal,$journals,$effects,$jobs,$journals,$effects,$jobs,0);" +
            "INSERT INTO main.recovery_capacity_members VALUES($journal,$journal);";
        command.ExecuteNonQuery();
    }

    internal static void VerifyCounts(SqliteConnection connection, SqliteTransaction transaction)
    {
        VerifyGlobal(connection, transaction);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.operation_journals j LEFT JOIN main.recovery_capacity_members m USING(journal_id) " +
            "WHERE m.journal_id IS NULL) OR EXISTS(SELECT 1 FROM main.recovery_capacity_members m JOIN main.operation_journals j USING(journal_id) " +
            "WHERE m.journal_id<>m.owner_journal_id AND (j.operation_kind<>'configExchange' OR NOT EXISTS " +
            "(SELECT 1 FROM configuration.configuration_run_resources r JOIN configuration.configuration_run_sessions s USING(session_id) " +
            "WHERE r.journal_id=m.journal_id AND s.parent_journal_id=m.owner_journal_id))) OR " +
            "EXISTS(SELECT 1 FROM main.recovery_capacity_reservations r WHERE NOT EXISTS " +
            "(SELECT 1 FROM main.recovery_capacity_members m WHERE m.journal_id=r.owner_journal_id AND m.owner_journal_id=r.owner_journal_id));";
        if ((long)command.ExecuteScalar()! != 0) throw Invalid();
        foreach (var item in ReadReservations(connection, transaction))
        {
            item.Limit.RequireValid();
            command.Parameters.Clear(); command.Parameters.AddWithValue("$owner", item.Owner.ToString("D"));
            command.CommandText = "SELECT (SELECT count(*) FROM main.recovery_capacity_members WHERE owner_journal_id=$owner)," +
                "(SELECT count(*) FROM main.file_effects f JOIN main.recovery_capacity_members m USING(journal_id) WHERE owner_journal_id=$owner)," +
                "(SELECT count(*) FROM main.journal_writer_jobs j JOIN main.recovery_capacity_members m USING(journal_id) WHERE owner_journal_id=$owner)," +
                "EXISTS(SELECT 1 FROM main.recovery_capacity_members m JOIN main.operation_journals j USING(journal_id) WHERE owner_journal_id=$owner AND phase<>'committed');";
            using var row = command.ExecuteReader();
            if (!row.Read() || row.GetInt64(0) + item.Remaining.Journals != item.Limit.Journals && !item.Released ||
                row.GetInt64(1) + item.Remaining.FileEffects != item.Limit.FileEffects && !item.Released ||
                row.GetInt64(2) + item.Remaining.Jobs != item.Limit.Jobs && !item.Released ||
                row.GetInt64(0) > item.Limit.Journals || row.GetInt64(1) > item.Limit.FileEffects || row.GetInt64(2) > item.Limit.Jobs ||
                item.Released && (row.GetInt64(3) != 0 || item.Remaining != new RecoveryCapacityBudget(0, 0, 0))) throw Invalid();
        }
    }

    internal static IEnumerable<(Guid Owner, RecoveryCapacityBudget Limit, RecoveryCapacityBudget Remaining, bool Released)>
        ReadReservations(SqliteConnection connection, SqliteTransaction transaction)
    {
        string after = "";
        while (true)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT owner_journal_id,journal_limit,effect_limit,job_limit,remaining_journals,remaining_effects,remaining_jobs,released " +
                "FROM main.recovery_capacity_reservations WHERE owner_journal_id>$after ORDER BY owner_journal_id LIMIT 128;";
            command.Parameters.AddWithValue("$after", after);
            var page = new List<(Guid Owner, RecoveryCapacityBudget Limit, RecoveryCapacityBudget Remaining, bool Released)>();
            using (var rows = command.ExecuteReader())
                while (rows.Read()) page.Add((Guid.ParseExact(rows.GetString(0), "D"), new(rows.GetInt32(1), rows.GetInt32(2), rows.GetInt32(3)),
                    new(rows.GetInt32(4), rows.GetInt32(5), rows.GetInt32(6)), rows.GetInt32(7) == 1));
            if (page.Count == 0) yield break;
            foreach (var item in page) yield return item;
            after = page[^1].Owner.ToString("D");
        }
    }

    internal static void VerifyGlobal(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = Usage;
        using var row = command.ExecuteReader();
        if (!row.Read() || row.GetInt64(0) > RecoveryCapacityBudget.MaximumJournals ||
            row.GetInt64(1) > RecoveryCapacityBudget.MaximumFileEffects || row.GetInt64(2) > RecoveryCapacityBudget.MaximumJobs)
            throw Invalid();
    }

    internal static IOException Invalid() => new("recovery_capacity_inventory_invalid");
}
