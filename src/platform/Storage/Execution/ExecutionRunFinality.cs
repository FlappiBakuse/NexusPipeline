using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

public sealed class ExecutionRunFinality(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal void RequireDatabase(StateDatabase expected)
    {
        if (expected.InstanceId != database.InstanceId || expected.RuntimeRoot != database.RuntimeRoot ||
            !StringComparer.OrdinalIgnoreCase.Equals(expected.DatabasePath, database.DatabasePath))
            throw new IOException("execution_retry_finality_unbound");
    }

    public bool IsFinal(AttemptReceipt receipt) => ReadDecision(receipt).IsFinal;

    public RunFinalityDecision ReadDecision(AttemptReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var transaction = connection.BeginTransaction(deferred: true);
        if (AttemptReceiptStore.ReadInside(connection, transaction, receipt.Facts.AttemptId) != receipt)
            throw new IOException("execution_retry_receipt_unbound");
        var source = StartSourceRows.Read(connection, transaction, receipt.OperationId);
        var journal = RecoveryJournalRegistry.ReadInside(connection, transaction, receipt.JournalId);
        var attempt = AttemptPreparedBinding.ReadPlan(instance, contracts, journal);
        StartSourceRows.RequireAttempt(source, attempt);
        if (journal.Phase != NexusPipeline.Core.Configuration.JournalPhase.Committed ||
            journal.PlanDigest != receipt.PlanDigest || source.RunId != receipt.Facts.RunId ||
            source.AttemptOrdinal != receipt.Facts.Ordinal) throw new IOException("execution_retry_receipt_unbound");
        var physical = new JournalStore(instance, contracts).Read(receipt.JournalId);
        var result = AttemptResultCodec.Read(physical.Root, attempt);
        var noEffect = NoEffectReceipt.Recorded(physical.Root);
        if (physical.Revision != journal.Revision || physical.Phase != journal.Phase ||
            (result is null) != noEffect || AttemptPreparedBinding.ExpectedReceipt(attempt, physical) != receipt)
            throw new IOException("execution_retry_receipt_unbound");
        if (noEffect) JournalEffectGate.Require(connection, transaction, receipt.JournalId,
            NexusPipeline.Core.Configuration.EffectGateState.Sealed);
        var total = new ExecutionPlanCodec(instance, contracts, new(instance, contracts))
            .Read(AttemptPlanCodec.Reference(attempt.ExecutionPlan)).ReadWire();
        _ = new RetryHookEvidence(instance, database, contracts).RequireAll(attempt, total, physical.Root, verifyContents: false);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.execution_start_sources WHERE run_id=$run AND attempt_ordinal>$ordinal);";
        command.Parameters.AddWithValue("$run", receipt.Facts.RunId.ToString("D"));
        command.Parameters.AddWithValue("$ordinal", receipt.Facts.Ordinal);
        if ((long)command.ExecuteScalar()! != 0) return new(false, null);
        var next = ExecutionRetryRows.After(connection, transaction, receipt.Facts.AttemptId);
        if (next is not null)
        {
            var declined = ExecutionRetryOutcomes.ReadReason(connection, transaction, next);
            return new(declined is not null, declined == "retry_budget_exhausted" && HasDeferredPost() ? "retry_post_final_not_executed" : declined);
        }
        RunBudgetObservation? terminal = null;
        _ = RunBudgetRows.Read(connection, transaction, receipt.Facts.RunId, item =>
        { if (item.JournalId == receipt.JournalId && item.TerminalReceiptDigest == receipt.SourceReceiptDigest) terminal = item; });
        // A receipt without the same-transaction measured boundary cannot establish run finality.
        if (terminal is null || terminal.ClockRegressed) return new(false, null);
        var decision = ExecutionRetryInventory.Decision(instance, database, contracts, attempt, total, receipt, terminal, physical.Root);
        var final = !decision.IsEligible && decision.ReasonCode != "retry_backoff";
        return new(final, final && HasDeferredPost() ? receipt.Facts.Lifecycle == LifecycleOutcome.Cancelled
            ? "retry_run_cancelled" : "retry_post_final_not_executed" : null);

        bool HasDeferredPost() => RetryHookCodec.Read(physical.Root, total).Any(item => item.Role == "postHook");
    }
}
