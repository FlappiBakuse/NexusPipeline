using System.Globalization;
using NexusPipeline.Core.Configuration;
using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class RunBudgetInventory
{
    internal static void Verify(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var inputs = new BudgetPlanInputs(instance, contracts);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT run_id FROM execution_run_budget_states ORDER BY run_id;";
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                _ = RunBudgetRows.Read(connection, transaction, Guid.ParseExact(reader.GetString(0), "D"),
                    value => RequireHistory(connection, transaction, instance, inputs, value)) ?? throw RunBudgetRows.Invalid();
        command.CommandText = "SELECT 1 FROM operations o LEFT JOIN execution_run_budget_observations b ON b.operation_id=o.operation_id WHERE o.operation_kind='execution.budget.observe' AND b.operation_id IS NULL LIMIT 1;";
        if (command.ExecuteScalar() is not null) throw RunBudgetRows.Invalid();
        command.CommandText = "SELECT 1 FROM execution_run_budget_observations b LEFT JOIN execution_run_budget_states s ON s.run_id=b.run_id WHERE s.run_id IS NULL LIMIT 1;";
        if (command.ExecuteScalar() is not null) throw RunBudgetRows.Invalid();
    }

    private static void RequireHistory(SqliteConnection connection, SqliteTransaction transaction, RuntimeInstance instance,
        BudgetPlanInputs inputs, RunBudgetObservation value)
    {
        if (value.OccurrenceId is { } occurrence)
        {
            using var source = connection.CreateCommand(); source.Transaction = transaction;
            source.CommandText = "SELECT EXISTS(SELECT 1 FROM startup_queue_occurrences WHERE occurrence_id=$id);";
            source.Parameters.AddWithValue("$id", occurrence.ToString("D"));
            if ((long)source.ExecuteScalar()! != 0)
            {
                var original = StartupQueueRows.Read(connection, transaction, occurrence, instance.InstanceId);
                if (original.Occurrence.RunId != value.RunId || original.Occurrence.StartOperationId != value.StartOperationId ||
                    value.SubjectId != SavedStartupQueue.Subject) throw RunBudgetRows.Invalid();
            }
            else
            {
                var original = ScheduleMinuteRows.ReadOccurrence(connection, transaction, occurrence, instance.InstanceId);
                if (original.Frozen.RunId != value.RunId || original.Frozen.StartOperationId != value.StartOperationId ||
                    original.SubjectId != value.SubjectId) throw RunBudgetRows.Invalid();
            }
        }
        if (value.JournalId is not { } journalId) return;
        var journal = RecoveryJournalRegistry.ReadInside(connection, transaction, journalId);
        var bound = inputs.Read(journal);
        var attempt = bound.Plan; var total = bound.Total;
        var originalStart = StartSourceRows.Original(connection, transaction,
            StartSourceRows.Read(connection, transaction, Guid.Parse(attempt.OperationId)));
        if (attempt.RunId != value.RunId.ToString("D") || originalStart.Identity.OperationId != value.StartOperationId ||
            total.Budget.TotalTimeoutSeconds * 1000 != value.TotalMilliseconds ||
            DateTimeOffset.Parse(total.RetryPolicy.DeadlineUtc, CultureInfo.InvariantCulture) != value.DeadlineUtc)
            throw RunBudgetRows.Invalid();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM operations WHERE operation_id=$operation AND initial_journal_id=$journal AND subject_id=$subject AND operation_kind='execution.start');";
        command.Parameters.AddWithValue("$operation", attempt.OperationId); command.Parameters.AddWithValue("$journal", attempt.JournalId);
        command.Parameters.AddWithValue("$subject", value.SubjectId);
        if ((long)command.ExecuteScalar()! != 1) throw RunBudgetRows.Invalid();
        if (value.TerminalReceiptDigest is not { } terminalDigest) return;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM attempt_receipts r JOIN operations o ON o.operation_id=r.operation_id WHERE r.attempt_id=$attempt AND r.run_id=$run AND r.journal_id=$journal AND r.operation_id=$operation AND r.source_receipt_digest=$digest AND o.subject_id=$subject AND o.status='completed');";
        command.Parameters.AddWithValue("$attempt", attempt.AttemptId); command.Parameters.AddWithValue("$run", attempt.RunId);
        command.Parameters.AddWithValue("$digest", terminalDigest);
        if ((long)command.ExecuteScalar()! != 1) throw RunBudgetRows.Invalid();
    }

    private sealed class BudgetPlanInputs(RuntimeInstance instance, ContractReader contracts) : IDisposable
    {
        private RegisteredJournal? _journal;
        private AttemptBoundInputs? _inputs;

        internal AttemptBoundInputs Read(RegisteredJournal journal)
        {
            if (_journal is null || _journal.JournalId != journal.JournalId || _journal.PlanPath != journal.PlanPath ||
                _journal.PlanDigest != journal.PlanDigest || _journal.OperationId != journal.OperationId ||
                _journal.Kind != journal.Kind || !_journal.ResourceKeys.SequenceEqual(journal.ResourceKeys))
            {
                _inputs?.Dispose(); _inputs = null; _journal = null;
                _inputs = AttemptPreparedBinding.ReadPlanInputs(instance, contracts, journal);
                _journal = journal;
            }
            return _inputs!;
        }

        public void Dispose() { _inputs?.Dispose(); _inputs = null; _journal = null; }
    }

}
