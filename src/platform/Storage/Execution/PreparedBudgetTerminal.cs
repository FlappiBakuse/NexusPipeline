using System.Collections.Immutable;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class PreparedBudgetTerminal : IDisposable
{
    private readonly InstanceLease _lease;
    private readonly StateDatabase _database;
    private readonly AcceptedExecutionAdmission _accepted;
    private readonly ResourceOperationBorrow _borrow;
    private readonly TimeProvider _clock;
    private bool _disposed;

    private PreparedBudgetTerminal(InstanceLease lease, StateDatabase database,
        AcceptedExecutionAdmission accepted, ResourceOperationBorrow borrow, TimeProvider clock)
    { _lease = lease; _database = database; _accepted = accepted; _borrow = borrow; _clock = clock; }

    internal static PreparedBudgetTerminal Capture(InstanceLease lease, StateDatabase database,
        AcceptedExecutionAdmission accepted, ResourceLeaseManager resources, TimeProvider clock)
    {
        lease.VerifyHeld(lease.Instance); lease.RunBudgets.RequireMeasured(accepted);
        if (database.InstanceId != lease.Instance.InstanceId || database.RuntimeRoot != lease.Instance.RuntimeRoot)
            throw RunBudgetRows.Invalid();
        var borrow = accepted.Root.Borrow(resources, accepted.Identity.OperationId,
            ExecutionPlanResources.Claims(accepted.Prepared.Total.Plan.ReadWire()));
        return new(lease, database, accepted, borrow, clock);
    }

    internal (RunBudgetObservation Measurement, ExecutionRetryIntent? Retry) Bind(AttemptWriterState state, JournalDocument document)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _borrow.Use(() =>
        {
            _lease.VerifyHeld(_lease.Instance); var prepared = _accepted.Prepared; prepared.RequireLive();
            if (state.Journal.JournalId != _accepted.Receipt.JournalId || state.Attempt.AttemptId != prepared.Attempt.Plan.ReadWire().AttemptId ||
                state.Journal.OperationId != _accepted.Identity.OperationId || document.JournalId != state.Journal.JournalId ||
                document.Phase != NexusPipeline.Core.Configuration.JournalPhase.Committed)
                throw RunBudgetRows.Invalid();
            var result = AttemptResultCodec.Read(document.Root, state.Attempt) ?? throw RunBudgetRows.Invalid();
            using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
            using var transaction = connection.BeginTransaction(deferred: true);
            var saved = RunBudgetRows.Read(connection, transaction, result.Facts.RunId) ?? throw RunBudgetRows.Invalid();
            var wire = prepared.Total.Plan.ReadWire();
            var original = StartSourceRows.Original(connection, transaction,
                StartSourceRows.Read(connection, transaction, _accepted.Identity.OperationId));
            if (saved.StartOperationId != original.Identity.OperationId || saved.SubjectId != _accepted.Identity.SubjectId ||
                saved.TotalMilliseconds != wire.Budget.TotalTimeoutSeconds * 1000 ||
                saved.DeadlineUtc != DateTimeOffset.Parse(wire.RetryPolicy.DeadlineUtc, System.Globalization.CultureInfo.InvariantCulture))
                throw RunBudgetRows.Invalid();
            var measured = prepared.TimeBudget.Read(); var observed = _clock.GetUtcNow();
            var measurement = new RunBudgetObservation("nexus.execution.run-budget", 1, saved.RunId, saved.StartOperationId,
                saved.OccurrenceId, saved.SubjectId, saved.TotalMilliseconds, saved.FirstPreparedAtUtc, saved.DeadlineUtc,
                measured.ConsumedMilliseconds, observed, saved.ClockRegressed || observed < saved.ObservedAtUtc,
                saved.Ordinal + 1, RunBudgetRows.Identity(saved).OperationId, document.JournalId,
                AttemptResultCodec.Receipt(state.Attempt, document, result.Facts).SourceReceiptDigest);
            return (measurement, RetryIntentCodec.Candidate(_lease.Instance, _accepted, state, document, measurement));
        });
    }

    public void Dispose() { if (_disposed) return; _disposed = true; _borrow.Dispose(); }
}
