using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class CompletionActionRows
{
    internal static CompletionActionSnapshot? Read(SqliteConnection connection, SqliteTransaction transaction,
        Guid operationId, string subject)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT subject_id,request_digest,frozen_json FROM completion_actions WHERE operation_id=$operation;";
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        string digest; CompletionActionGroup frozen;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            if (reader.GetString(0) != subject) throw new UnauthorizedAccessException("completion_subject_mismatch");
            digest = reader.GetString(1); frozen = CompletionActionCodec.Decode<CompletionActionGroup>(reader.GetString(2));
            if (digest != CompletionActionCodec.Identity(operationId, subject, frozen).RequestDigest) throw CompletionActionCodec.Invalid();
        }
        var identity = CompletionActionCodec.Identity(operationId, subject, frozen);
        var operation = OperationRows.ReadExisting(connection, transaction, identity) ?? throw CompletionActionCodec.Invalid();
        command.CommandText = "SELECT ordinal,snapshot_json FROM completion_action_transitions WHERE operation_id=$operation ORDER BY ordinal;";
        CompletionActionSnapshot? prior = null; var ordinal = 0;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (reader.GetInt32(0) != ordinal++ || ordinal > 17) throw CompletionActionCodec.Invalid();
                var next = CompletionActionCodec.Decode<CompletionActionSnapshot>(reader.GetString(1));
                if (next.OperationId != operationId || next.SubjectId != subject || CompletionActionCodec.Encode(next.Frozen) != CompletionActionCodec.Encode(frozen))
                    throw CompletionActionCodec.Invalid();
                if (prior is null) CompletionActionCodec.RequireInitial(next);
                else CompletionActionCodec.RequireTransition(prior, next);
                prior = next;
            }
        }
        if (prior is null) throw CompletionActionCodec.Invalid();
        var ended = prior.Phase is CompletionEffectPhase.Accepted or CompletionEffectPhase.Cancelled or CompletionEffectPhase.Failed or CompletionEffectPhase.Unknown;
        if (operation.Status != (ended ? "completed" : "accepted")) throw CompletionActionCodec.Invalid();
        command.CommandText = "SELECT c.run_id,c.intent_json FROM completion_action_members m JOIN completion_candidates c ON c.run_id=m.run_id WHERE m.operation_id=$operation ORDER BY c.run_id;";
        var expected = frozen.Intents.ToDictionary(item => item.RunId); var seen = new HashSet<Guid>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var run = Guid.ParseExact(reader.GetString(0), "D");
                if (!seen.Add(run) || !expected.TryGetValue(run, out var intent) || reader.GetString(1) != CompletionActionCodec.Encode(intent))
                    throw CompletionActionCodec.Invalid();
            }
        if (seen.Count != expected.Count) throw CompletionActionCodec.Invalid();
        return prior;
    }

    internal static void Insert(SqliteConnection connection, SqliteTransaction transaction, CompletionActionSnapshot snapshot)
    {
        CompletionActionCodec.RequireInitial(snapshot);
        var identity = CompletionActionCodec.Identity(snapshot.OperationId, snapshot.SubjectId, snapshot.Frozen);
        if (!OperationRows.Accept(connection, transaction, identity, snapshot.AcceptedAtUtc).IsNew) throw CompletionActionCodec.Invalid();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO completion_actions VALUES($operation,$subject,$digest,$frozen);";
        command.Parameters.AddWithValue("$operation", snapshot.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$subject", snapshot.SubjectId);
        command.Parameters.AddWithValue("$digest", identity.RequestDigest);
        command.Parameters.AddWithValue("$frozen", CompletionActionCodec.Encode(snapshot.Frozen)); command.ExecuteNonQuery();
        foreach (var intent in snapshot.Frozen.Intents)
        {
            command.CommandText = "INSERT INTO completion_action_members VALUES($operation,$run);";
            command.Parameters.AddWithValue("$run", intent.RunId.ToString("D")); command.ExecuteNonQuery(); command.Parameters.RemoveAt("$run");
        }
        InsertTransition(connection, transaction, snapshot, 0);
    }

    internal static void Append(SqliteConnection connection, SqliteTransaction transaction, CompletionActionSnapshot expected,
        CompletionActionSnapshot next)
    {
        var current = Read(connection, transaction, expected.OperationId, expected.SubjectId) ?? throw CompletionActionCodec.Invalid();
        if (CompletionActionCodec.Encode(current) != CompletionActionCodec.Encode(expected)) throw new IOException("completion_revision_conflict");
        CompletionActionCodec.RequireTransition(current, next);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM completion_action_transitions WHERE operation_id=$operation;";
        command.Parameters.AddWithValue("$operation", current.OperationId.ToString("D"));
        InsertTransition(connection, transaction, next, checked((int)(long)command.ExecuteScalar()!));
        if (next.Phase is CompletionEffectPhase.Cancelled or CompletionEffectPhase.Failed or CompletionEffectPhase.Accepted or CompletionEffectPhase.Unknown)
        {
            command.CommandText = "UPDATE operations SET status='completed' WHERE operation_id=$operation AND status='accepted';";
            if (command.ExecuteNonQuery() != 1) throw CompletionActionCodec.Invalid();
        }
    }

    private static void InsertTransition(SqliteConnection connection, SqliteTransaction transaction, CompletionActionSnapshot snapshot, int ordinal)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO completion_action_transitions VALUES($operation,$ordinal,$snapshot);";
        command.Parameters.AddWithValue("$operation", snapshot.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$ordinal", ordinal);
        command.Parameters.AddWithValue("$snapshot", CompletionActionCodec.Encode(snapshot)); command.ExecuteNonQuery();
    }
}
