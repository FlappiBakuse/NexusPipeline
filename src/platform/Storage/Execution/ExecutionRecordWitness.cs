using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class ExecutionRecordWitness(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal CompletedRunRecord Read(AttemptReceipt receipt, RecoveredRecordTiming? recovery = null)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        RequireFinalInside(connection, transaction, receipt);
        var source = StartSourceRows.Read(connection, transaction, receipt.OperationId);
        var original = StartSourceRows.Original(connection, transaction, source);
        var journal = RecoveryJournalRegistry.ReadInside(connection, transaction, receipt.JournalId);
        var attempt = AttemptPreparedBinding.ReadPlan(instance, contracts, journal);
        StartSourceRows.RequireAttempt(source, attempt);
        var total = new ExecutionPlanCodec(instance, contracts, new(instance, contracts))
            .Read(AttemptPlanCodec.Reference(attempt.ExecutionPlan)).ReadWire();
        if (total.RunId != receipt.Facts.RunId.ToString("D")) throw Invalid();
        var (automation, user) = original.Request.Target switch
        {
            QueueTarget queue when total.QueueId == queue.QueueId.ToString("D") => (queue.QueueId, (Guid?)null),
            ScriptTarget script when total.Steps.All(step => step.ScriptId == script.ScriptId.ToString("D")) =>
                (script.ScriptId, total.Selection.Select(item => item.UserId).Distinct(StringComparer.Ordinal).ToArray() is [var only]
                    ? Guid.ParseExact(only, "D") : (Guid?)null),
            ScriptUserTarget selected when total.Steps.All(step => step.ScriptId == selected.ScriptId.ToString("D") &&
                step.UserId == selected.UserId.ToString("D")) => (selected.ScriptId, (Guid?)selected.UserId),
            BindingTarget binding => Binding(binding.BindingId),
            _ => throw Invalid()
        };
        RunBudgetObservation? terminal = null;
        _ = RunBudgetRows.Read(connection, transaction, receipt.Facts.RunId, observation =>
        {
            if (observation.JournalId == receipt.JournalId && observation.TerminalReceiptDigest == receipt.SourceReceiptDigest)
                terminal = observation;
        });
        if (recovery is null && (terminal is null || terminal.ClockRegressed)) throw Invalid();
        var ended = recovery?.ObservedAtUtc ?? terminal!.ObservedAtUtc;
        var retry = ExecutionRetryRows.After(connection, transaction, receipt.Facts.AttemptId);
        if (retry is not null)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT o.created_at FROM execution_retry_outcomes d JOIN operations o ON o.operation_id=d.operation_id WHERE d.retry_operation_id=$retry;";
            command.Parameters.AddWithValue("$retry", RetryIntentCodec.Identity(retry).OperationId.ToString("D"));
            var closed = DateTimeOffset.ParseExact(command.ExecuteScalar() as string ?? throw Invalid(), "O",
                System.Globalization.CultureInfo.InvariantCulture);
            if (closed.Offset != TimeSpan.Zero) throw Invalid();
            if (closed > ended) ended = closed;
        }
        var record = new CompletedRunRecord(automation, user, ended, recovery is null ? terminal!.ConsumedMilliseconds : recovery.CheckpointMilliseconds,
            receipt.Facts, recovery is null ? retry is null ? RecordDurationKind.Measured : RecordDurationKind.CheckpointLowerBound
                : recovery.CheckpointMilliseconds.HasValue ? RecordDurationKind.CheckpointLowerBound : RecordDurationKind.Unknown,
            recovery is null ? retry is null ? EndTimeKind.MeasuredTerminal : EndTimeKind.DeclinedRetryObservation
                : EndTimeKind.ReconciledTerminalObservation);
        record.Validate();
        return record;

        (Guid, Guid?) Binding(Guid bindingId)
        {
            var selected = total.Selection.SingleOrDefault(item => item.BindingId == bindingId.ToString("D")) ?? throw Invalid();
            if (total.Selection.Count != 1) throw Invalid();
            return (Guid.ParseExact(selected.ScriptId, "D"), Guid.ParseExact(selected.UserId, "D"));
        }
    }

    internal static void RequireFinalInside(SqliteConnection connection, SqliteTransaction transaction, AttemptReceipt receipt)
    {
        if (AttemptReceiptStore.ReadInside(connection, transaction, receipt.Facts.AttemptId) != receipt) throw Invalid();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM execution_start_sources WHERE run_id=$run AND attempt_ordinal>$ordinal);";
        command.Parameters.AddWithValue("$run", receipt.Facts.RunId.ToString("D"));
        command.Parameters.AddWithValue("$ordinal", receipt.Facts.Ordinal);
        if ((long)command.ExecuteScalar()! != 0) throw Invalid();
        var next = ExecutionRetryRows.After(connection, transaction, receipt.Facts.AttemptId);
        if (next is not null && ExecutionRetryOutcomes.ReadReason(connection, transaction, next) is null) throw Invalid();
    }

    private static IOException Invalid() => new("record_execution_source_unbound");
}
