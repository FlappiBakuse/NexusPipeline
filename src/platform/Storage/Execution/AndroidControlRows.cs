using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class AndroidControlRows
{
    internal static void RegisterInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, AndroidCommandRegistration item, AndroidCommandRequest request)
    {
        AndroidControlAdmission.RequireInside(connection, transaction, state, request);
        if (item.Binding != request.Binding || item.Sequence != request.Sequence ||
            item.RequestDigest != AndroidCommandIdentity.Digest(request)) throw Invalid();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO main.execution_target_command_jobs(journal_id,command_id,step_id,command_sequence,job_name,writer_id,document_json) " +
            "VALUES($journal,$id,$step,$sequence,$job,$writer,$document) ON CONFLICT(journal_id,command_id) DO NOTHING;";
        Bind(command, item);
        command.Parameters.AddWithValue("$step", item.Binding.StepId); command.Parameters.AddWithValue("$sequence", item.Sequence);
        command.Parameters.AddWithValue("$job", item.Binding.JobName); command.Parameters.AddWithValue("$writer", item.Binding.OwnerWriterId.ToString("D"));
        command.Parameters.AddWithValue("$document", System.Text.Encoding.UTF8.GetString(
            NexusPipeline.Platform.Serialization.BoundedJson.Serialize(item, StepResultCodec.Options)));
        command.ExecuteNonQuery();
        RequireRegisteredInside(connection, transaction, item);
    }

    internal static void RequireRegisteredInside(SqliteConnection connection, SqliteTransaction transaction, AndroidCommandRegistration item)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT step_id,command_sequence,job_name,writer_id,document_json FROM main.execution_target_command_jobs " +
            "WHERE journal_id=$journal AND command_id=$id;";
        Bind(command, item); using var row = command.ExecuteReader();
        if (!row.Read() || row.GetString(0) != item.Binding.StepId || row.GetInt32(1) != item.Sequence ||
            row.GetString(2) != item.Binding.JobName || row.GetString(3) != item.Binding.OwnerWriterId.ToString("D")) throw Invalid();
        var saved = AndroidControlCodec.Decode<AndroidCommandRegistration>(System.Text.Encoding.UTF8.GetBytes(row.GetString(4)));
        if (saved != item) throw Invalid();
    }

    internal static void VerifyInside(SqliteConnection connection, SqliteTransaction transaction,
        Guid journalId, IReadOnlyList<AndroidCommandRegistration> projected)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT command_id FROM main.execution_target_command_jobs WHERE journal_id=$journal LIMIT 1025;";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        var ids = new List<Guid>();
        using (var row = command.ExecuteReader()) while (row.Read()) ids.Add(Guid.Parse(row.GetString(0)));
        if (ids.Count != projected.Count || !ids.ToHashSet().SetEquals(projected.Select(item => item.Binding.TargetId))) throw Invalid();
        foreach (var item in projected) RequireRegisteredInside(connection, transaction, item);
    }

    private static void Bind(SqliteCommand command, AndroidCommandRegistration item)
    {
        command.Parameters.AddWithValue("$journal", item.Binding.ParentJournalId.ToString("D"));
        command.Parameters.AddWithValue("$id", item.Binding.TargetId.ToString("D"));
    }

    private static IOException Invalid() => new("execution_android_command_sql_unbound");
}
