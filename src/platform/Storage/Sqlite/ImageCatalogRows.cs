using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class ImageCatalogRows
{
    private const string SelectEntries = "SELECT position,step_id,capture_id,width,height,outcome,reason_code," +
        "source_path,source_size,source_sha256,root_path,root_size,root_sha256,result_path,result_size,result_sha256," +
        "png_path,png_size,png_sha256 FROM execution_image_entries ";

    internal static void PublishInside(SqliteConnection connection, SqliteTransaction transaction, ImageCatalogSnapshot expected)
    {
        RequireCursor(connection, transaction, expected);
        using (var exists = Command(connection, transaction, "SELECT 1 FROM execution_image_catalog WHERE attempt_id=$0;",
            expected.Receipt.Facts.AttemptId.ToString("D")))
            if (exists.ExecuteScalar() is not null) { RequireSaved(connection, transaction, expected); return; }
        using var header = Command(connection, transaction, "INSERT INTO execution_image_catalog VALUES($0,$1,$2,$3,$4,$5);",
            expected.Receipt.Facts.AttemptId.ToString("D"), expected.Receipt.Facts.RunId.ToString("D"),
            expected.Receipt.JournalId.ToString("D"), expected.TerminalRevision, expected.TerminalSha256, expected.Entries.Count);
        header.ExecuteNonQuery();
        foreach (var entry in expected.Entries)
        {
            var item = entry.Item;
            using var insert = Command(connection, transaction,
                "INSERT INTO execution_image_entries VALUES($0,$1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19);",
                item.Key.AttemptId.ToString("D"), item.Position, item.Key.StepId, item.Key.CaptureId.ToString("D"),
                Value(item.Width), Value(item.Height), (int)item.Outcome, item.ReasonCode,
                entry.Source.RelativePath, entry.Source.SizeBytes, entry.Source.Sha256,
                Value(entry.Root?.RelativePath), Value(entry.Root?.SizeBytes), Value(entry.Root?.Sha256),
                entry.Result.RelativePath, entry.Result.SizeBytes, entry.Result.Sha256,
                Value(entry.Png?.RelativePath), Value(entry.Png?.SizeBytes), Value(entry.Png?.Sha256));
            insert.ExecuteNonQuery();
        }
        RequireSaved(connection, transaction, expected);
    }

    internal static void RequireSaved(SqliteConnection connection, SqliteTransaction transaction, ImageCatalogSnapshot expected)
    {
        RequireCursor(connection, transaction, expected);
        var receipt = expected.Receipt;
        using var header = Command(connection, transaction,
            "SELECT run_id,journal_id,terminal_revision,terminal_sha256,entry_count FROM execution_image_catalog WHERE attempt_id=$0;",
            receipt.Facts.AttemptId.ToString("D"));
        using (var row = header.ExecuteReader())
            if (!row.Read() || row.GetString(0) != receipt.Facts.RunId.ToString("D") || row.GetString(1) != receipt.JournalId.ToString("D") ||
                row.GetInt64(2) != expected.TerminalRevision || row.GetString(3) != expected.TerminalSha256 ||
                row.GetInt32(4) != expected.Entries.Count) throw Invalid();
        using var entries = Command(connection, transaction, SelectEntries + "WHERE attempt_id=$0 ORDER BY position;",
            receipt.Facts.AttemptId.ToString("D"));
        using var rows = entries.ExecuteReader(); var index = 0;
        while (rows.Read())
            if (index >= expected.Entries.Count || ReadEntry(rows, receipt.Facts.RunId, receipt.Facts.AttemptId) != expected.Entries[index++])
                throw Invalid();
        if (index != expected.Entries.Count) throw Invalid();
    }

    internal static SqliteCommand PageCommand(SqliteConnection connection, SqliteTransaction transaction, ImageCatalogQuery query)
    {
        ImageCatalogPolicy.Validate(query);
        return Command(connection, transaction, SelectEntries + "WHERE attempt_id=$0 AND position>$1 ORDER BY position LIMIT $2;",
            query.AttemptId.ToString("D"), query.AfterPosition, query.Limit + 1);
    }

    internal static ImageCatalogEntry ReadEntry(SqliteDataReader row, Guid run, Guid attempt)
    {
        try
        {
            var key = new RunImageKey(run, attempt, row.GetString(1), Guid.ParseExact(row.GetString(2), "D"));
            ImageCatalogPolicy.Validate(key);
            var source = Plan(row, 7); var root = Optional(row, 10, true);
            var result = Plan(row, 13); var png = Optional(row, 16, false);
            var outcome = (RunImageOutcome)row.GetInt32(5);
            int? width = row.IsDBNull(3) ? null : row.GetInt32(3);
            int? height = row.IsDBNull(4) ? null : row.GetInt32(4);
            if (row.GetInt32(0) is < 0 or > 4095 || width.HasValue != height.HasValue ||
                width is < 1 or > 8192 || height is < 1 or > 8192 || (long?)width * height > 33554432 ||
                outcome == RunImageOutcome.Captured && width is null ||
                !Enum.IsDefined(outcome) || row.GetString(6).Length is < 1 or > 120 ||
                (outcome == RunImageOutcome.Captured) != (png is not null) || png is not null && root is null)
                throw Invalid();
            return new(new(row.GetInt32(0), key, outcome, row.GetString(6), width, height,
                source.Sha256, root?.Sha256, result.Sha256, png?.Sha256, png?.SizeBytes), source, root, result, png);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or System.Text.Json.JsonException or OverflowException)
        { throw new IOException("record_image_catalog_unbound", error); }
    }

    private static BlobReference Plan(SqliteDataReader row, int index)
    {
        var reference = new BlobReference(row.GetString(index), row.GetInt64(index + 1), row.GetString(index + 2));
        StepEvidenceCodec.Plan(reference); return reference;
    }

    private static BlobReference? Optional(SqliteDataReader row, int index, bool plan)
    {
        if (row.IsDBNull(index) && row.IsDBNull(index + 1) && row.IsDBNull(index + 2)) return null;
        if (row.IsDBNull(index) || row.IsDBNull(index + 1) || row.IsDBNull(index + 2)) throw Invalid();
        if (plan) return Plan(row, index);
        var png = new BlobReference(row.GetString(index), row.GetInt64(index + 1), row.GetString(index + 2));
        if (png.SizeBytes is < 57 or > ExecutionCaptureInputs.MaximumPngBytes ||
            !NexusPipeline.Core.Configuration.FileTreePolicy.Digest(png.Sha256) ||
            png.RelativePath != $"blobs/content/{png.Sha256}.bin") throw Invalid();
        return png;
    }

    private static void RequireCursor(SqliteConnection connection, SqliteTransaction transaction, ImageCatalogSnapshot expected)
    {
        var receipt = expected.Receipt;
        if (AttemptReceiptStore.ReadInside(connection, transaction, receipt.Facts.AttemptId) != receipt ||
            StartSourceRows.Read(connection, transaction, receipt.OperationId) != expected.StartSource) throw Invalid();
        using var command = Command(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM operation_journals j JOIN operations o ON o.operation_id=j.operation_id " +
            "WHERE j.journal_id=$0 AND j.operation_id=$1 AND j.phase='committed' AND j.revision=$2 AND j.plan_digest=$3 " +
            "AND o.operation_kind='execution.start' AND o.status='completed' AND o.initial_journal_id=j.journal_id);",
            receipt.JournalId.ToString("D"), receipt.OperationId.ToString("D"), expected.TerminalRevision, receipt.PlanDigest);
        if ((long)command.ExecuteScalar()! != 1) throw Invalid();
    }

    private static object Value(object? value) => value ?? DBNull.Value;
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, params object[] values)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        for (var index = 0; index < values.Length; index++) command.Parameters.AddWithValue("$" + index, values[index]);
        return command;
    }
    internal static IOException Invalid() => new("record_image_catalog_unbound");
}
