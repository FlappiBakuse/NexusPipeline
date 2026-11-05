using System.Globalization;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed record RecoveredRecordTiming(DateTimeOffset ObservedAtUtc, long? CheckpointMilliseconds)
{
    internal static RecoveredRecordTiming Capture(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, AttemptReceipt receipt)
    {
        var bound = AttemptPreparedBinding.Read(instance, contracts, database, receipt.JournalId);
        var physical = new JournalStore(instance, contracts).Read(receipt.JournalId);
        if (!bound.IsExecutionStart || physical.Phase != JournalPhase.Committed ||
            AttemptPreparedBinding.ExpectedReceipt(bound.Plan, physical) != receipt) throw Invalid();
        AttemptTerminalEvidence.RequireStopped(database, receipt.JournalId);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        ExecutionRecordWitness.RequireFinalInside(connection, transaction, receipt);
        var source = StartSourceRows.Read(connection, transaction, receipt.OperationId);
        StartSourceRows.RequireAttempt(source, bound.Plan);
        RunBudgetObservation? terminal = null;
        var latest = RunBudgetRows.Read(connection, transaction, receipt.Facts.RunId, value =>
        {
            if (value.JournalId == receipt.JournalId && value.TerminalReceiptDigest == receipt.SourceReceiptDigest) terminal = value;
        });
        if (terminal is { ClockRegressed: false }) throw Invalid();
        var observed = DateTimeOffset.Parse(physical.Root.GetProperty("updatedAt").GetString()!, CultureInfo.InvariantCulture);
        if (observed == default || observed.Offset != TimeSpan.Zero) throw Invalid();
        // A stopped owner cannot reconstruct the uncheckpointed monotonic tail from UTC timestamps.
        return new(observed, latest?.ConsumedMilliseconds);
    }

    private static IOException Invalid() => new("record_recovered_closure_unbound");
}
