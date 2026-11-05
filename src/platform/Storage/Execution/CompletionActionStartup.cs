using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class CompletionActionStartup
{
    internal static void Verify(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        CompletionIntentSource source)
    {
        lease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot ||
            !StringComparer.OrdinalIgnoreCase.Equals(database.DatabasePath, Path.Combine(instance.RuntimeRoot, "state", "nexus.db")))
            throw CompletionActionCodec.Invalid();
        VerifyInventory(database, source.Require);
    }

    internal static void VerifyReadOnly(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
    {
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot) throw CompletionActionCodec.Invalid();
        var finality = new ExecutionRunFinality(instance, database, contracts);
        VerifyInventory(database, candidate =>
        {
            var receipt = new AttemptReceiptStore(database).Read(candidate.Intent.AttemptId) ?? throw CompletionActionCodec.Invalid();
            if (CompletionIntentSource.ReadSaved(instance, database, contracts, finality, receipt) != candidate) throw CompletionActionCodec.Invalid();
        });
    }

    private static void VerifyInventory(StateDatabase database, Action<BoundCompletionIntent> requireSource)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        foreach (var page in Pages(connection, transaction, "completion_candidates", "run_id"))
            foreach (var id in page) VerifyCandidate(connection, transaction, id, requireSource);
        foreach (var page in Pages(connection, transaction, "completion_actions", "operation_id"))
            foreach (var id in page)
            {
                var action = CompletionActionRows.Read(connection, transaction, Guid.ParseExact(id, "D"), CompletionActionStore.HostSubject)
                    ?? throw CompletionActionCodec.Invalid();
                foreach (var intent in action.Frozen.Intents)
                {
                    var candidate = ReadCandidate(connection, transaction, intent.RunId.ToString("D"));
                    if (candidate.Intent != intent) throw CompletionActionCodec.Invalid();
                }
            }
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM operations o WHERE (o.operation_kind='execution.completion.candidate' AND NOT EXISTS(SELECT 1 FROM completion_candidates c WHERE c.operation_id=o.operation_id)) OR (o.operation_kind='execution.completion' AND NOT EXISTS(SELECT 1 FROM completion_actions a WHERE a.operation_id=o.operation_id)) LIMIT 1;";
        if (command.ExecuteScalar() is not null) throw CompletionActionCodec.Invalid();
        command.CommandText = "SELECT 1 FROM completion_candidate_outcomes o LEFT JOIN completion_candidates c ON c.run_id=o.run_id LEFT JOIN completion_action_members m ON m.run_id=o.run_id WHERE c.run_id IS NULL OR m.run_id IS NOT NULL OR o.reason_code NOT IN ('completion_action_conflict','completion_cancelled','completion_local_authorization_required','completion_lifecycle_unavailable','completion_owner_restarted') LIMIT 1;";
        if (command.ExecuteScalar() is not null) throw CompletionActionCodec.Invalid();
    }

    internal static void ReconcileStoppedOwner(StateDatabase database, InstanceLease lease, RecoveryIndex recovery)
    {
        lease.VerifyHeld(lease.Instance);
        var holds = new List<RecoveryResourceHold>();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        foreach (var page in Pages(connection, transaction, "completion_actions", "operation_id"))
            foreach (var id in page)
            {
                var action = CompletionActionRows.Read(connection, transaction, Guid.ParseExact(id, "D"), CompletionActionStore.HostSubject)
                    ?? throw CompletionActionCodec.Invalid();
                var next = action.Phase switch
                {
                    CompletionEffectPhase.Prepared or CompletionEffectPhase.Countdown => action with
                    { Phase = CompletionEffectPhase.Cancelled, ReasonCode = "completion_owner_restarted" },
                    CompletionEffectPhase.Entered => action with
                    { Phase = CompletionEffectPhase.Unknown, ReasonCode = "completion_owner_restarted" },
                    _ => action
                };
                if (next != action)
                    lease.PublishExecution(() => database.Write((writer, writeTransaction) =>
                    { CompletionActionRows.Append(writer, writeTransaction, action, next); return true; }));
                if (next.Phase == CompletionEffectPhase.Unknown)
                {
                    if (holds.Count == 4096) throw new IOException("completion_unknown_capacity");
                    holds.Add(new(next.OperationId, "completion:state", "completion_native_outcome_unknown"));
                }
            }
        recovery.UpdateResourceHolds(RecoveryHoldOwner.CompletionActions, holds);
    }

    private static void VerifyCandidate(SqliteConnection connection, SqliteTransaction transaction, string run, Action<BoundCompletionIntent> requireSource)
    {
        var candidate = ReadCandidate(connection, transaction, run); requireSource(candidate);
        var expected = CompletionCandidateStore.Identity(candidate);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT operation_id,attempt_id,owner_generation FROM completion_candidates WHERE run_id=$run;";
        command.Parameters.AddWithValue("$run", run);
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(0) != expected.OperationId.ToString("D") || reader.GetString(1) != candidate.Intent.AttemptId.ToString("D") ||
                !Guid.TryParseExact(reader.GetString(2), "D", out var generation) || generation == Guid.Empty || generation.ToString("D") != reader.GetString(2))
                throw CompletionActionCodec.Invalid();
        }
        if (OperationRows.ReadExisting(connection, transaction, expected)?.Status != "completed") throw CompletionActionCodec.Invalid();
    }

    private static BoundCompletionIntent ReadCandidate(SqliteConnection connection, SqliteTransaction transaction, string run)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT intent_json,total_plan_path,total_plan_size,total_plan_sha256,subject_id FROM completion_candidates WHERE run_id=$run;";
        command.Parameters.AddWithValue("$run", run);
        using var reader = command.ExecuteReader(); if (!reader.Read()) throw CompletionActionCodec.Invalid();
        var intent = CompletionActionCodec.Decode<FrozenCompletionIntent>(reader.GetString(0));
        if (intent.RunId.ToString("D") != run || CompletionActionCodec.Encode(intent) != reader.GetString(0)) throw CompletionActionCodec.Invalid();
        return new(intent, new(reader.GetString(1), reader.GetInt64(2), reader.GetString(3)), reader.GetString(4));
    }

    private static IEnumerable<IReadOnlyList<string>> Pages(SqliteConnection connection, SqliteTransaction transaction, string table, string key)
    {
        if ((table, key) is not (("completion_candidates", "run_id") or ("completion_actions", "operation_id")))
            throw new ArgumentException("completion_inventory_query_invalid");
        var after = "";
        while (true)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = $"SELECT {key} FROM {table} WHERE {key}>$after ORDER BY {key} LIMIT 128;";
            command.Parameters.AddWithValue("$after", after); var page = new List<string>();
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    var id = reader.GetString(0);
                    if (!Guid.TryParseExact(id, "D", out var parsed) || parsed == Guid.Empty || parsed.ToString("D") != id) throw CompletionActionCodec.Invalid();
                    page.Add(id);
                }
            if (page.Count == 0) yield break;
            yield return page.AsReadOnly(); after = page[^1];
        }
    }
}
