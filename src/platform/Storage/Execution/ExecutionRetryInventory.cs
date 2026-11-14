using System.Text.Json;
using System.Globalization;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionRetryInventory
{
    internal static void Verify(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
    {
        ExecutionRetryCancellation.VerifyInventory(database);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        foreach (var id in ReadIds(connection, transaction, retries: true))
        {
            var retry = ExecutionRetryRows.Read(connection, transaction, id);
            var source = StartSourceRows.Read(connection, transaction, retry.PreviousOperationId);
            var parent = RecoveryJournalRegistry.ReadInside(connection, transaction, retry.PreviousJournalId);
            using var inputs = AttemptPreparedBinding.ReadPlanInputs(instance, contracts, parent);
            var attempt = inputs.Plan;
            StartSourceRows.RequireAttempt(source, attempt);
            var totalRef = AttemptPlanCodec.Reference(attempt.ExecutionPlan);
            if (retry.InstanceId != instance.InstanceId || retry.PreviousPlan != AttemptPreparedBinding.Reference(instance, parent) ||
                retry.TotalPlan != totalRef || retry.Frozen.PreviousAttemptId.ToString("D") != attempt.AttemptId ||
                retry.Frozen.NextOrdinal != attempt.AttemptOrdinal + 1 || source.Identity.SubjectId != retry.SubjectId)
                throw RetryIntentCodec.Invalid();
            var total = inputs.Total;
            var receipt = AttemptReceiptStore.ReadInside(connection, transaction, Guid.Parse(attempt.AttemptId))
                ?? throw RetryIntentCodec.Invalid();
            var terminal = Terminal(connection, transaction, receipt);
            var decision = Decision(instance, database, contracts, attempt, total, receipt, terminal, new JournalStore(instance, contracts).Read(receipt.JournalId).Root);
            if (!decision.IsEligible && decision.ReasonCode != "retry_backoff" ||
                retry.Frozen.NotBefore != decision.NotBefore || retry.Frozen.DeadlineUtc != DateTimeOffset.Parse(total.RetryPolicy.DeadlineUtc, CultureInfo.InvariantCulture) ||
                retry.Frozen.IdempotencyKey != total.RetryPolicy.IdempotencyKey) throw RetryIntentCodec.Invalid();
            var declined = ExecutionRetryOutcomes.HasOutcome(connection, transaction, retry);
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.operations WHERE operation_id=$next);";
            command.Parameters.Clear(); command.Parameters.AddWithValue("$next", retry.Frozen.NextOperationId.ToString("D"));
            if ((long)command.ExecuteScalar()! == 0) continue;
            if (declined) throw RetryIntentCodec.Invalid();
            var next = StartSourceRows.Read(connection, transaction, retry.Frozen.NextOperationId);
            var nextParent = RecoveryJournalRegistry.ReadInside(connection, transaction, next.JournalId);
            var nextAttempt = AttemptPreparedBinding.ReadPlan(instance, contracts, nextParent);
            if (next.RetrySourceOperationId != source.Identity.OperationId || next.JournalId != retry.Frozen.NextJournalId ||
                next.RunId != retry.Frozen.RunId || next.AttemptOrdinal != retry.Frozen.NextOrdinal ||
                nextAttempt.AttemptId != retry.Frozen.NextAttemptId.ToString("D") ||
                AttemptPlanCodec.Reference(nextAttempt.ExecutionPlan) != totalRef) throw RetryIntentCodec.Invalid();
        }
        command.Parameters.Clear();
        command.CommandText = "SELECT 1 FROM main.operations o LEFT JOIN main.execution_retry_intents r ON r.operation_id=o.operation_id WHERE o.operation_kind='execution.retry.prepare' AND r.operation_id IS NULL LIMIT 1;";
        if (command.ExecuteScalar() is not null) throw RetryIntentCodec.Invalid();
        command.CommandText = "SELECT 1 FROM main.operations o LEFT JOIN main.execution_retry_outcomes r ON r.operation_id=o.operation_id WHERE o.operation_kind='execution.retry.decline' AND r.operation_id IS NULL LIMIT 1;";
        if (command.ExecuteScalar() is not null) throw RetryIntentCodec.Invalid();
        foreach (var run in ReadIds(connection, transaction, retries: false))
            _ = RunBudgetRows.Read(connection, transaction, run, boundary =>
            {
                if (boundary.TerminalReceiptDigest is null || boundary.JournalId is not { } journal) return;
                var receipt = ReadTerminal(connection, transaction, run, journal, boundary.TerminalReceiptDigest);
                if (RetryDecision.Classify(receipt.Facts) != AttemptOutcome.Interrupted) return;
                var parent = RecoveryJournalRegistry.ReadInside(connection, transaction, journal);
                using var inputs = AttemptPreparedBinding.ReadPlanInputs(instance, contracts, parent);
                var attempt = inputs.Plan;
                if (receipt.Facts.AttemptId.ToString("D") != attempt.AttemptId) throw RetryIntentCodec.Invalid();
                var total = inputs.Total;
                var decision = Decision(instance, database, contracts, attempt, total, receipt, boundary, new JournalStore(instance, contracts).Read(receipt.JournalId).Root);
                if ((decision.IsEligible || decision.ReasonCode == "retry_backoff") &&
                    ExecutionRetryRows.After(connection, transaction, receipt.Facts.AttemptId) is null) throw RetryIntentCodec.Invalid();
            });
    }

    private static AttemptReceipt ReadTerminal(SqliteConnection connection, SqliteTransaction transaction,
        Guid run, Guid journal, string digest)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT attempt_id FROM records.attempt_receipts WHERE journal_id=$journal AND run_id=$run AND source_receipt_digest=$digest;";
        command.Parameters.AddWithValue("$journal", journal.ToString("D"));
        command.Parameters.AddWithValue("$run", run.ToString("D")); command.Parameters.AddWithValue("$digest", digest);
        var id = command.ExecuteScalar() as string ?? throw RetryIntentCodec.Invalid();
        return AttemptReceiptStore.ReadInside(connection, transaction, Guid.ParseExact(id, "D")) ?? throw RetryIntentCodec.Invalid();
    }

    private static IEnumerable<Guid> ReadIds(SqliteConnection connection, SqliteTransaction transaction, bool retries)
    {
        var after = "";
        while (true)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = retries
                ? "SELECT operation_id FROM main.execution_retry_intents WHERE operation_id>$after ORDER BY operation_id LIMIT 64;"
                : "SELECT run_id FROM main.execution_run_budget_states WHERE run_id>$after ORDER BY run_id LIMIT 64;";
            command.Parameters.AddWithValue("$after", after);
            var page = new List<string>(64);
            using (var reader = command.ExecuteReader()) while (reader.Read()) page.Add(reader.GetString(0));
            foreach (var id in page) yield return Guid.ParseExact(id, "D");
            if (page.Count < 64) yield break;
            after = page[^1];
        }
    }

    internal static RunBudgetObservation Terminal(SqliteConnection connection, SqliteTransaction transaction, AttemptReceipt receipt)
    {
        RunBudgetObservation? found = null;
        _ = RunBudgetRows.Read(connection, transaction, receipt.Facts.RunId, item =>
        { if (item.JournalId == receipt.JournalId && item.TerminalReceiptDigest == receipt.SourceReceiptDigest) found = item; });
        return found ?? throw RetryIntentCodec.Invalid();
    }

    internal static RetryDecision Decision(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        NexusPipeline.Platform.Serialization.Protocol.RunAttemptPlan attempt, NexusPipeline.Platform.Serialization.Protocol.ExecutionPlan total, AttemptReceipt receipt,
        RunBudgetObservation boundary, JsonElement journal)
    {
        if (RetryDecision.Classify(receipt.Facts) != AttemptOutcome.Interrupted) return new(false, "attempt_not_retryable", null);
        if (!ExecutionInvocationHistory.CanResume(total, journal, ExecutionInvocationHistory.Before(instance, database, contracts, attempt, total)))
            return new(false, "attempt_budget_exhausted", null);
        return RetryDecision.Evaluate(RetryIntentCodec.Policy(total),
            new(RetryDecision.Classify(receipt.Facts), WriterState.Stopped,
                receipt.Facts.Recovery is RecoveryOutcome.Restored or RecoveryOutcome.NotRequired, false, RetryHookEvidence.CanRetry(journal, total),
                receipt.Facts.Ordinal, boundary.ObservedAtUtc),
            new(boundary.ConsumedMilliseconds, boundary.TotalMilliseconds == 0 ? TimeSpan.MaxValue : TimeSpan.FromMilliseconds(boundary.TotalMilliseconds - boundary.ConsumedMilliseconds),
                boundary.ClockRegressed ? "clock_unknown" : null), boundary.ObservedAtUtc);
    }
}
