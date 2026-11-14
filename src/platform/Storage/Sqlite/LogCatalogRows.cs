using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class LogCatalogRows
{
    private const string SelectEntries = "SELECT position,step_id,role,raw_relative_path,raw_size_bytes,raw_sha256,observed_bytes,recorded_bytes,discarded_bytes,is_complete,part_count FROM records.execution_log_entries ";
    internal static void PublishInside(SqliteConnection connection, SqliteTransaction transaction, LogCatalogSnapshot expected)
    {
        RequireCursor(connection, transaction, expected);
        if (Exists(connection, transaction, expected.Receipt.Facts.AttemptId))
        { RequireSaved(connection, transaction, expected); return; }
        using var header = Command(connection, transaction,
            "INSERT INTO records.execution_log_catalog VALUES($0,$1,$2,$3,$4,$5);",
            expected.Receipt.Facts.AttemptId.ToString("D"), expected.Receipt.Facts.RunId.ToString("D"),
            expected.Receipt.JournalId.ToString("D"), expected.TerminalRevision, expected.TerminalSha256, expected.Entries.Count);
        header.ExecuteNonQuery();
        foreach (var entry in expected.Entries)
        {
            var item = entry.Item;
            using var insert = Command(connection, transaction,
                "INSERT INTO records.execution_log_entries VALUES($0,$1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11);",
                item.Key.AttemptId.ToString("D"), item.Position, item.Key.StepId, (int)item.Key.Role,
                entry.Raw.RelativePath, entry.Raw.SizeBytes, entry.Raw.Sha256, item.ObservedBytes, item.RecordedBytes,
                item.DiscardedBytes, item.IsComplete ? 1 : 0, item.PartCount);
            insert.ExecuteNonQuery();
        }
        RequireSaved(connection, transaction, expected);
    }

    internal static bool Exists(SqliteConnection connection, SqliteTransaction transaction, Guid attempt)
    {
        using var command = Command(connection, transaction,
            "SELECT 1 FROM records.execution_log_catalog WHERE attempt_id=$0;", attempt.ToString("D"));
        return command.ExecuteScalar() is not null;
    }

    internal static void RequireSaved(SqliteConnection connection, SqliteTransaction transaction, LogCatalogSnapshot expected)
    {
        RequireCursor(connection, transaction, expected);
        var receipt = expected.Receipt;
        using var header = Command(connection, transaction,
            "SELECT run_id,journal_id,terminal_revision,terminal_sha256,entry_count FROM records.execution_log_catalog WHERE attempt_id=$0;",
            receipt.Facts.AttemptId.ToString("D"));
        using (var row = header.ExecuteReader())
            if (!row.Read() || row.GetString(0) != receipt.Facts.RunId.ToString("D") || row.GetString(1) != receipt.JournalId.ToString("D") ||
                row.GetInt64(2) != expected.TerminalRevision || row.GetString(3) != expected.TerminalSha256 || row.GetInt32(4) != expected.Entries.Count)
                throw Invalid();
        using var entries = Command(connection, transaction, SelectEntries + "WHERE attempt_id=$0 ORDER BY position;", receipt.Facts.AttemptId.ToString("D"));
        using var actual = entries.ExecuteReader(); var index = 0;
        while (actual.Read())
        {
            if (index >= expected.Entries.Count || ReadEntry(actual, receipt.Facts.RunId, receipt.Facts.AttemptId) != expected.Entries[index++])
                throw Invalid();
        }
        if (index != expected.Entries.Count) throw Invalid();
    }

    internal static SqliteCommand PageCommand(SqliteConnection connection, SqliteTransaction transaction, LogCatalogQuery query)
    {
        LogCatalogPolicy.Validate(query);
        return Command(connection, transaction,
            SelectEntries + "WHERE attempt_id=$0 AND position>$1 ORDER BY position LIMIT $2;",
            query.AttemptId.ToString("D"), query.AfterPosition, query.Limit + 1);
    }

    internal static LogCatalogEntry ReadEntry(SqliteDataReader row, Guid run, Guid attempt)
    {
        var position = row.GetInt32(0); var step = row.GetString(1); var role = (RunLogRole)row.GetInt32(2);
        var raw = new BlobReference(row.GetString(3), row.GetInt64(4), row.GetString(5));
        try { StepEvidenceCodec.Plan(raw); }
        catch (System.Text.Json.JsonException error) { throw new IOException("record_log_catalog_unbound", error); }
        if (position is < 0 or > 4095 || step is not { Length: > 0 and <= 120 } ||
            step.Any(character => character is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.')) ||
            !Enum.IsDefined(role) || row.GetInt64(6) < 0 || row.GetInt64(7) is < 0 or > RawOutputCodec.MaximumRecordedBytes ||
            row.GetInt64(8) < 0 || row.GetInt64(6) - row.GetInt64(8) != row.GetInt64(7) ||
            row.GetInt32(9) is not (0 or 1) || row.GetInt32(10) is < 0 or > RawOutputCodec.MaximumParts) throw Invalid();
        return new(new(position, new(run, attempt, step, role), raw.Sha256, row.GetInt64(6), row.GetInt64(7),
            row.GetInt64(8), row.GetInt32(9) == 1, row.GetInt32(10)), raw);
    }

    private static void RequireCursor(SqliteConnection connection, SqliteTransaction transaction, LogCatalogSnapshot expected)
    {
        var receipt = expected.Receipt;
        if (AttemptReceiptStore.ReadInside(connection, transaction, receipt.Facts.AttemptId) != receipt) throw Invalid();
        using var command = Command(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM main.operation_journals j JOIN main.operations o ON o.operation_id=j.operation_id " +
            "WHERE j.journal_id=$0 AND j.operation_id=$1 AND j.phase='committed' AND j.revision=$2 AND j.plan_digest=$3 " +
            "AND o.operation_kind='execution.start' AND o.status='completed' AND o.initial_journal_id=j.journal_id);",
            receipt.JournalId.ToString("D"), receipt.OperationId.ToString("D"), expected.TerminalRevision, receipt.PlanDigest);
        if ((long)command.ExecuteScalar()! != 1) throw Invalid();
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, params object[] values)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        for (var index = 0; index < values.Length; index++) command.Parameters.AddWithValue("$" + index, values[index]);
        return command;
    }

    internal static IOException Invalid() => new("record_log_catalog_unbound");
}
