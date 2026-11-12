using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Sqlite;

public sealed class AttemptReceiptStore(StateDatabase database) : IAttemptReceiptReader
{
    internal void ProjectNoEffect(RunAttemptPlan plan, JournalDocument document)
    {
        if (document.Kind != JournalKind.RunAttempt || document.Phase != JournalPhase.Committed || !NoEffectReceipt.Recorded(document.Root) ||
            document.InstanceId != database.InstanceId || document.JournalId != Guid.Parse(plan.JournalId) ||
            document.Root.GetProperty("operationId").GetString() != plan.OperationId || document.Root.GetProperty("runId").GetString() != plan.RunId ||
            document.Root.GetProperty("attemptId").GetString() != plan.AttemptId) throw new IOException("attempt_receipt_unbound");
        database.Write((connection, transaction) =>
        {
            JournalEffectGate.Require(connection, transaction, document.JournalId, EffectGateState.Sealed);
            ProjectInside(connection, transaction, plan, document, NoEffectReceipt.Facts(plan));
            return true;
        });
    }

    internal static void ProjectInside(SqliteConnection connection, SqliteTransaction transaction, RunAttemptPlan plan,
        JournalDocument document, AttemptFacts facts)
    {
        if (document.Kind != JournalKind.RunAttempt || document.Phase != JournalPhase.Committed || document.JournalId != Guid.Parse(plan.JournalId) ||
            document.Root.GetProperty("operationId").GetString() != plan.OperationId || facts.RunId != Guid.Parse(plan.RunId) ||
            facts.AttemptId != Guid.Parse(plan.AttemptId) || facts.Ordinal != plan.AttemptOrdinal) throw new IOException("attempt_receipt_unbound");
        var receipt = AttemptResultCodec.Receipt(plan, document, facts);
        var prior = ReadInside(connection, transaction, receipt.Facts.AttemptId);
        if (prior is not null)
        {
            if (prior != receipt) throw new IOException("attempt_receipt_replay_conflict");
            CompleteOperation(connection, transaction, receipt);
            return;
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO attempt_receipts VALUES($attempt,$run,$ordinal,$journal,$operation,$plan,$source,$engine,$business,$lifecycle,$recovery,$reason);";
        command.Parameters.AddWithValue("$attempt", receipt.Facts.AttemptId.ToString("D"));
        command.Parameters.AddWithValue("$run", receipt.Facts.RunId.ToString("D"));
        command.Parameters.AddWithValue("$ordinal", receipt.Facts.Ordinal);
        command.Parameters.AddWithValue("$journal", receipt.JournalId.ToString("D"));
        command.Parameters.AddWithValue("$operation", receipt.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$plan", receipt.PlanDigest);
        command.Parameters.AddWithValue("$source", receipt.SourceReceiptDigest);
        command.Parameters.AddWithValue("$engine", (int)receipt.Facts.Engine);
        command.Parameters.AddWithValue("$business", (int)receipt.Facts.Business);
        command.Parameters.AddWithValue("$lifecycle", (int)receipt.Facts.Lifecycle);
        command.Parameters.AddWithValue("$recovery", (int)receipt.Facts.Recovery);
        command.Parameters.AddWithValue("$reason", receipt.Facts.ReasonCode);
        command.ExecuteNonQuery();
        CompleteOperation(connection, transaction, receipt);
    }

    private static void CompleteOperation(SqliteConnection connection, SqliteTransaction transaction, AttemptReceipt receipt)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE operations SET status='completed' WHERE operation_id=$operation AND initial_journal_id=$journal AND status IN ('accepted','completed');";
        command.Parameters.AddWithValue("$operation", receipt.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$journal", receipt.JournalId.ToString("D"));
        if (command.ExecuteNonQuery() != 1) throw new IOException("attempt_receipt_operation_unbound");
    }

    public AttemptReceipt? Read(Guid attemptId)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        return ReadInside(connection, null, attemptId);
    }

    internal static AttemptReceipt? ReadInside(SqliteConnection connection, SqliteTransaction? transaction, Guid attemptId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT run_id,ordinal,journal_id,operation_id,plan_digest,source_receipt_digest,engine_outcome,business_outcome,lifecycle_outcome,recovery_outcome,reason_code FROM attempt_receipts WHERE attempt_id=$id;";
        command.Parameters.AddWithValue("$id", attemptId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var facts = new AttemptFacts(Guid.Parse(reader.GetString(0)), attemptId, reader.GetInt32(1), (EngineOutcome)reader.GetInt32(6),
            (BusinessOutcome)reader.GetInt32(7), (LifecycleOutcome)reader.GetInt32(8), (RecoveryOutcome)reader.GetInt32(9), reader.GetString(10));
        return new(Guid.Parse(reader.GetString(2)), Guid.Parse(reader.GetString(3)), reader.GetString(4), reader.GetString(5), facts);
    }
}
