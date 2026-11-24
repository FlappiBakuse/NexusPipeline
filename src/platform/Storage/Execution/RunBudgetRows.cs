using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record RunBudgetObservation(string Format, int Version, Guid RunId, Guid StartOperationId,
    Guid? OccurrenceId, string SubjectId, long TotalMilliseconds, DateTimeOffset FirstPreparedAtUtc, DateTimeOffset DeadlineUtc,
    long ConsumedMilliseconds, DateTimeOffset ObservedAtUtc, bool ClockRegressed, int Ordinal, Guid? PreviousOperationId, Guid? JournalId, string? TerminalReceiptDigest = null);

internal static class RunBudgetRows
{
    internal const int MaximumObservations = 4096;

    internal static OperationIdentity Identity(RunBudgetObservation value) =>
        Identity(value, BoundedJson.Serialize(value, StepResultCodec.Options));

    private static OperationIdentity Identity(RunBudgetObservation value, byte[] bytes)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("nexus.execution.budget/1/" + value.RunId.ToString("D") + "/" +
            value.Ordinal.ToString(CultureInfo.InvariantCulture))).AsSpan(0, 16), bigEndian: true);
        return new(id, value.SubjectId, digest, "execution.budget.observe");
    }

    internal static RunBudgetObservation? Read(SqliteConnection connection, SqliteTransaction transaction, Guid runId,
        Action<RunBudgetObservation>? verifyHistorical = null) =>
        ReadHead(connection, transaction, runId, verifyHistorical)?.Observation;

    private static VerifiedHead? ReadHead(SqliteConnection connection, SqliteTransaction transaction, Guid runId,
        Action<RunBudgetObservation>? verifyHistorical = null)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT last_operation_id,observation_count FROM main.execution_run_budget_states WHERE run_id=$run;";
        command.Parameters.AddWithValue("$run", runId.ToString("D"));
        Guid last; int expected;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            last = Guid.ParseExact(reader.GetString(0), "D"); expected = reader.GetInt32(1);
            if (expected is < 1 or > MaximumObservations) throw Invalid();
        }
        command.CommandText = "SELECT b.document_json,o.operation_id,o.subject_id,o.request_digest,o.operation_kind,o.status,b.journal_id FROM main.execution_run_budget_observations b JOIN main.operations o ON o.operation_id=b.operation_id WHERE b.run_id=$run ORDER BY b.ordinal;";
        RunBudgetObservation? before = null;
        OperationIdentity? precedingIdentity = null;
        var ordinal = 0;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var bytes = Encoding.UTF8.GetBytes(reader.GetString(0)); using var json = StrictJson.Parse(bytes);
                var value = json.RootElement.Deserialize<RunBudgetObservation>(StepResultCodec.Options) ?? throw Invalid();
                if (!JsonElement.DeepEquals(json.RootElement, JsonSerializer.SerializeToElement(value, StepResultCodec.Options))) throw Invalid();
                RequireObservation(value, runId, ordinal, before, precedingIdentity?.OperationId);
                var identity = Identity(value);
                if (identity.OperationId.ToString("D") != reader.GetString(1) || identity.SubjectId != reader.GetString(2) ||
                    identity.RequestDigest != reader.GetString(3) || identity.Kind != reader.GetString(4) || reader.GetString(5) != "completed" ||
                    (reader.IsDBNull(6) ? (Guid?)null : Guid.ParseExact(reader.GetString(6), "D")) != value.JournalId) throw Invalid();
                verifyHistorical?.Invoke(value);
                before = value;
                precedingIdentity = identity;
                ordinal++;
            }
        }
        if (ordinal != expected || before is null || precedingIdentity is null || precedingIdentity.OperationId != last) throw Invalid();
        return new(before, precedingIdentity);
    }

    internal static void Append(SqliteConnection connection, SqliteTransaction transaction, RunBudgetObservation next)
    {
        var prior = ReadHead(connection, transaction, next.RunId);
        if (next.Ordinal >= MaximumObservations) throw Invalid();
        RequireObservation(next, next.RunId, prior?.Observation.Ordinal + 1 ?? 0, prior?.Observation, prior?.Identity.OperationId);
        var bytes = BoundedJson.Serialize(next, StepResultCodec.Options);
        var identity = Identity(next, bytes);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT total_changes();";
        var changes = (long)command.ExecuteScalar()!;
        if (!OperationRows.Accept(connection, transaction, identity, next.ObservedAtUtc).IsNew) throw Invalid();
        command.CommandText = "INSERT INTO main.execution_run_budget_observations(operation_id,run_id,ordinal,document_json,journal_id) VALUES($id,$run,$ordinal,$json,$journal);";
        command.Parameters.AddWithValue("$id", identity.OperationId.ToString("D")); command.Parameters.AddWithValue("$run", next.RunId.ToString("D"));
        command.Parameters.AddWithValue("$journal", next.JournalId is { } journal ? journal.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$ordinal", next.Ordinal);
        command.Parameters.AddWithValue("$json", Encoding.UTF8.GetString(bytes));
        if (command.ExecuteNonQuery() != 1) throw Invalid();
        if (prior is null)
            command.CommandText = """
                INSERT INTO main.execution_run_budget_states(run_id,last_operation_id,observation_count)
                SELECT $run,$id,1 WHERE NOT EXISTS(
                    SELECT 1 FROM main.execution_run_budget_observations WHERE run_id=$run AND ordinal>0);
                """;
        else
        {
            command.CommandText = "UPDATE main.execution_run_budget_states SET last_operation_id=$id,observation_count=observation_count+1 WHERE run_id=$run AND last_operation_id=$previous AND observation_count=$ordinal;";
            command.Parameters.AddWithValue("$previous", prior.Identity.OperationId.ToString("D"));
        }
        if (command.ExecuteNonQuery() != 1) throw Invalid();
        command.CommandText = "UPDATE main.operations SET status='completed' WHERE operation_id=$id;";
        if (command.ExecuteNonQuery() != 1) throw Invalid();

        // The writer verified the prefix under this transaction. Extra writes by a trigger invalidate that proof.
        command.CommandText = """
            SELECT 1 FROM main.execution_run_budget_states s
              JOIN main.execution_run_budget_observations b ON b.run_id=s.run_id AND b.operation_id=s.last_operation_id
              JOIN main.operations o ON o.operation_id=b.operation_id
             WHERE s.run_id=$run AND s.last_operation_id=$id AND s.observation_count=$ordinal+1
               AND b.ordinal=$ordinal AND b.document_json=$json AND b.journal_id IS $journal
               AND o.subject_id=$subject AND o.request_digest=$digest AND o.operation_kind=$kind AND o.status='completed'
               AND total_changes()=$changes;
            """;
        command.Parameters.AddWithValue("$subject", identity.SubjectId);
        command.Parameters.AddWithValue("$digest", identity.RequestDigest);
        command.Parameters.AddWithValue("$kind", identity.Kind);
        command.Parameters.AddWithValue("$changes", changes + 4); // Accepted operation, observation, cursor, completed status.
        if (command.ExecuteScalar() is null) throw Invalid();
    }

    private static void RequireObservation(RunBudgetObservation value, Guid runId, int ordinal,
        RunBudgetObservation? before, Guid? previousOperationId)
    {
        if (value.Format != "nexus.execution.run-budget" || value.Version != 1 || value.RunId != runId ||
            value.TerminalReceiptDigest is not null && (!NexusPipeline.Core.Configuration.FileTreePolicy.Digest(value.TerminalReceiptDigest) || value.JournalId is null) ||
            value.StartOperationId == Guid.Empty || value.OccurrenceId == Guid.Empty || value.JournalId == Guid.Empty ||
            value.OccurrenceId is null && value.JournalId is null || string.IsNullOrWhiteSpace(value.SubjectId) ||
            value.SubjectId.Length > 256 || value.SubjectId.Any(char.IsControl) || value.TotalMilliseconds is < 0 or > 604800000 ||
            value.FirstPreparedAtUtc.Offset != TimeSpan.Zero || value.DeadlineUtc.Offset != TimeSpan.Zero || value.ObservedAtUtc.Offset != TimeSpan.Zero ||
            value.FirstPreparedAtUtc == default || value.DeadlineUtc == default ||
            value.TotalMilliseconds == 0 && value.DeadlineUtc != DateTimeOffset.MaxValue ||
            value.ConsumedMilliseconds < 0 || value.TotalMilliseconds != 0 && value.ConsumedMilliseconds > value.TotalMilliseconds ||
            value.Ordinal != ordinal || value.PreviousOperationId != previousOperationId ||
            before is not null && (value.StartOperationId != before.StartOperationId || value.OccurrenceId != before.OccurrenceId ||
                value.SubjectId != before.SubjectId || value.TotalMilliseconds != before.TotalMilliseconds ||
                value.FirstPreparedAtUtc != before.FirstPreparedAtUtc || value.DeadlineUtc != before.DeadlineUtc ||
                value.ConsumedMilliseconds < before.ConsumedMilliseconds || before.ClockRegressed && !value.ClockRegressed ||
                value.ObservedAtUtc < before.ObservedAtUtc && !value.ClockRegressed)) throw Invalid();
    }

    private sealed record VerifiedHead(RunBudgetObservation Observation, OperationIdentity Identity);

    internal static IOException Invalid() => new("execution_run_budget_unbound");
}
