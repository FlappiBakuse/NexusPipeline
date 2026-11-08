using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Sqlite;

public sealed class JournalEffectGate(StateDatabase database) : IJournalEffectGate
{
    public EffectGateSnapshot Read(Guid journalId) => HistoryReadScope.ReadDatabase(database,
        (connection, transaction) => Read(connection, transaction, journalId).Gate);

    public EffectGateSnapshot Enter(RegisteredJournal expected, long expectedGateRevision)
        => Move(expected, expectedGateRevision, EffectGateState.Entered);

    public EffectGateSnapshot SealUnentered(RegisteredJournal expected, long expectedGateRevision)
        => Move(expected, expectedGateRevision, EffectGateState.Sealed);

    internal EffectGateSnapshot EnterExecution(AttemptWriterState state, string stepId, string role = "process")
        => Move(state.Journal, state.Gate.Revision, EffectGateState.Entered, (connection, transaction) =>
        {
            AttemptWriterState.RequireCursor(connection, transaction, state.Journal, EffectGateState.Unentered);
            ExecutionStepAdmission.RequireOpenRoles(state.Total, state.Jobs, stepId, role, state.Latest.Root);
            ExecutionStepAdmission.RequireJobsInside(connection, transaction, state.Journal.JournalId, state.AllJobs);
        });

    internal EffectGateSnapshot EnterExtension(RegisteredJournal expected, long expectedGateRevision,
        Action<SqliteConnection, SqliteTransaction> verify)
    {
        if (expected.Kind != JournalKind.ExtensionApply) throw new IOException("extension_effect_kind_unbound");
        return Move(expected, expectedGateRevision, EffectGateState.Entered, verify);
    }

    internal EffectGateSnapshot EnterMaintenance(RegisteredJournal expected, long expectedGateRevision,
        Action<SqliteConnection, SqliteTransaction> verify)
    {
        if (expected.Kind != JournalKind.UpdateApply) throw new IOException("maintenance_effect_kind_unbound");
        return Move(expected, expectedGateRevision, EffectGateState.Entered, verify);
    }

    internal static void Require(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, EffectGateState expected,
        bool requireApplying = false)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT j.operation_kind,g.state,j.phase,g.revision FROM operation_journals j LEFT JOIN journal_effect_gates g USING(journal_id) WHERE j.journal_id=$journal;";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new IOException("effect_gate_journal_missing");
        if (reader.GetString(0) is not ("runAttempt" or "updateApply" or "extensionApply" or "assetPrepare" or "secretPrepare")) return;
        if (reader.IsDBNull(1) || reader.GetString(1) != expected.ToString().ToLowerInvariant() ||
            reader.IsDBNull(3) || reader.GetInt64(3) != (expected == EffectGateState.Unentered ? 1 : 2) || requireApplying && reader.GetString(2) != "applying")
            throw new IOException("effect_gate_decision_required");
    }

    private EffectGateSnapshot Move(RegisteredJournal expected, long expectedRevision, EffectGateState next,
        Action<SqliteConnection, SqliteTransaction>? verify = null)
    {
        return database.Write((connection, transaction) =>
        {
            var current = Read(connection, transaction, expected.JournalId);
            if (current.Gate.OperationId != expected.OperationId || current.Gate.Kind != expected.Kind ||
                current.Gate.PlanDigest != expected.PlanDigest || current.PlanPath != expected.PlanPath ||
                current.JournalPhase != JournalPhase.Prepared || expected.Phase != JournalPhase.Prepared ||
                current.JournalRevision != expected.Revision || !current.Resources.SequenceEqual(expected.ResourceKeys.Order(StringComparer.Ordinal)))
                throw new IOException("effect_gate_journal_binding_changed");
            if (current.Gate.Revision != expectedRevision) throw new IOException("effect_gate_revision_conflict");
            if (next == EffectGateState.Sealed && current.Gate.State == EffectGateState.Sealed) return current.Gate;
            if (current.Gate.State != EffectGateState.Unentered) throw new IOException("effect_gate_already_decided");
            verify?.Invoke(connection, transaction);
            if (next == EffectGateState.Entered && expected.Kind == JournalKind.RunAttempt)
                ExecutionCancellationRows.RequireDispatchOpen(connection, transaction, expected.JournalId);
            var revision = checked(expectedRevision + 1);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE journal_effect_gates SET state=$state,revision=$revision WHERE journal_id=$journal AND revision=$expected AND state='unentered';";
            command.Parameters.AddWithValue("$state", next.ToString().ToLowerInvariant());
            command.Parameters.AddWithValue("$revision", revision);
            command.Parameters.AddWithValue("$journal", expected.JournalId.ToString("D"));
            command.Parameters.AddWithValue("$expected", expectedRevision);
            if (command.ExecuteNonQuery() != 1) throw new IOException("effect_gate_revision_conflict");
            command.CommandText = "UPDATE recovery_index_state SET revision=revision+1,ready=0 WHERE singleton=1;";
            if (command.ExecuteNonQuery() != 1) throw new IOException("effect_gate_index_missing");
            return current.Gate with { Revision = revision, State = next };
        });
    }

    private (EffectGateSnapshot Gate, JournalPhase JournalPhase, long JournalRevision, string PlanPath, string[] Resources)
        Read(SqliteConnection connection, SqliteTransaction transaction, Guid journalId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT j.operation_id,j.operation_kind,j.plan_digest,g.revision,g.state,j.phase,j.revision,j.plan_path FROM operation_journals j JOIN journal_effect_gates g USING(journal_id) WHERE j.journal_id=$journal;";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        EffectGateSnapshot gate;
        JournalPhase phase;
        long journalRevision;
        string planPath;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new IOException("effect_gate_missing");
            gate = new(database.InstanceId, journalId, Guid.Parse(reader.GetString(0)), Enum.Parse<JournalKind>(reader.GetString(1), true),
                reader.GetString(2), reader.GetInt64(3), Enum.Parse<EffectGateState>(reader.GetString(4), true));
            if (gate.Kind is not (JournalKind.RunAttempt or JournalKind.UpdateApply or JournalKind.ExtensionApply or JournalKind.AssetPrepare or JournalKind.SecretPrepare) ||
                gate.Revision != (gate.State == EffectGateState.Unentered ? 1 : 2)) throw new IOException("effect_gate_state_revision_unbound");
            phase = Enum.Parse<JournalPhase>(reader.GetString(5), true);
            journalRevision = reader.GetInt64(6);
            planPath = reader.GetString(7);
        }
        command.CommandText = "SELECT resource_key FROM journal_resources WHERE journal_id=$journal ORDER BY resource_key LIMIT 4097;";
        using var resources = command.ExecuteReader();
        var keys = new List<string>();
        while (resources.Read()) keys.Add(resources.GetString(0));
        if (keys.Count is < 1 or > 4096) throw new IOException("effect_gate_resources_invalid");
        return (gate, phase, journalRevision, planPath, keys.ToArray());
    }
}
