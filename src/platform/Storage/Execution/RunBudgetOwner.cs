using System.Globalization;
using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class RunBudgetOwner(InstanceLease lease)
{
    private const int MaximumMeasuredRuns = 4096;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, MeasuredRun> _clocks = [];

    internal DateTimeOffset? PreparedAt(StateDatabase database, Guid runId)
    {
        Require(database);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        return RunBudgetRows.Read(connection, transaction, runId)?.FirstPreparedAtUtc;
    }

    [SupportedOSPlatform("windows")]
    internal ExecutionTimeBudget BindScheduled(StateDatabase database, ResourceLeaseManager resources,
        PreparedExecutionOrigin origin, ExecutionPlanDraft draft, TimeProvider clock, long startedTimestamp, DateTimeOffset firstPreparedAt)
    {
        Require(database); origin.RequireRequest(draft.Explanation.Selection.Request);
        var wire = (draft.ReadPlan() ?? throw RunBudgetRows.Invalid()).ReadWire();
        if (wire.RunId != origin.RunId.ToString("D")) throw RunBudgetRows.Invalid();
        var deadline = DateTimeOffset.Parse(wire.RetryPolicy.DeadlineUtc, CultureInfo.InvariantCulture);
        var total = TimeSpan.FromSeconds(wire.Budget.TotalTimeoutSeconds);
        using var write = origin.AcquireMetadata(resources);
        return lease.PublishExecution(() =>
        {
            RunBudgetObservation? saved;
            using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
            using (var transaction = connection.BeginTransaction(deferred: true))
                saved = RunBudgetRows.Read(connection, transaction, origin.RunId);
            var budget = GetOrCreate(origin.RunId, origin.OperationId, total, deadline, saved, clock, startedTimestamp, firstPreparedAt);
            if (saved is null)
            {
                var snapshot = budget.Read(); var observed = clock.GetUtcNow();
                var observation = new RunBudgetObservation("nexus.execution.run-budget", 1, origin.RunId,
                    origin.OperationId, origin.OccurrenceId, origin.SubjectId, checked((long)total.TotalMilliseconds),
                    firstPreparedAt, deadline, snapshot.ConsumedMilliseconds, observed, observed < firstPreparedAt, 0, null, null);
                try
                {
                    database.Write((connection, transaction) =>
                    { RunBudgetRows.Append(connection, transaction, observation); return true; });
                }
                catch { lock (_gate) _clocks.Remove(origin.RunId); throw; }
            }
            return budget;
        });
    }

    internal void RecordAccepted(StateDatabase database, PreparedExecutionAdmission prepared, ResourceOperationLease root,
        ResourceLeaseManager resources, TimeProvider clock)
    {
        prepared.RequireLive(); var wire = prepared.Total.Plan.ReadWire(); var runId = Guid.Parse(wire.RunId);
        using var borrow = root.Borrow(resources, prepared.Identity.OperationId, ExecutionPlanResources.Claims(wire));
        borrow.Use(() => lease.PublishExecution(() =>
        {
            Require(database);
            var saved = Read(database, runId);
            Guid initialOperation;
            using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
            using (var transaction = connection.BeginTransaction(deferred: true))
                initialOperation = StartSourceRows.Original(connection, transaction,
                    StartSourceRows.Read(connection, transaction, prepared.Identity.OperationId)).Identity.OperationId;
            var total = TimeSpan.FromSeconds(wire.Budget.TotalTimeoutSeconds);
            var deadline = DateTimeOffset.Parse(wire.RetryPolicy.DeadlineUtc, CultureInfo.InvariantCulture);
            if (saved is not null && (saved.StartOperationId != initialOperation || saved.TotalMilliseconds != (long)total.TotalMilliseconds ||
                saved.DeadlineUtc != deadline)) throw RunBudgetRows.Invalid();
            lock (_gate)
            {
                if (_clocks.TryGetValue(runId, out var existing))
                { if (!ReferenceEquals(existing.Budget, prepared.TimeBudget)) throw RunBudgetRows.Invalid(); }
                else
                {
                    if (_clocks.Count >= MaximumMeasuredRuns) throw new IOException("execution_budget_capacity");
                    _clocks.Add(runId, new(prepared.TimeBudget, initialOperation, total, deadline));
                }
            }
            if (saved is null)
            {
                if (prepared.FirstPreparedAtUtc == default) throw RunBudgetRows.Invalid();
                var observed = clock.GetUtcNow();
                if (initialOperation != prepared.Identity.OperationId) throw RunBudgetRows.Invalid();
                var initial = new RunBudgetObservation("nexus.execution.run-budget", 1, runId, prepared.Identity.OperationId,
                    null, prepared.Identity.SubjectId, checked((long)total.TotalMilliseconds), prepared.FirstPreparedAtUtc, deadline,
                    prepared.TimeBudget.Read().ConsumedMilliseconds, observed, observed < prepared.FirstPreparedAtUtc, 0, null,
                    Guid.Parse(prepared.Attempt.Plan.ReadWire().JournalId));
                database.Write((connection, transaction) => { RunBudgetRows.Append(connection, transaction, initial); return true; });
            }
            else Append(database, runId, prepared.Identity.SubjectId, prepared.TimeBudget, clock.GetUtcNow(),
                Guid.Parse(prepared.Attempt.Plan.ReadWire().JournalId), saved, total, deadline);
            lock (_gate)
            {
                var measured = _clocks[runId];
                measured.AcceptedRun ??= root.AcceptRun(resources, prepared.Identity.OperationId, runId);
                if (!measured.AcceptedRun.IsActive) throw new IOException("execution_run_inactive");
            }
            return true;
        }));
    }

    internal PreparedBudgetTerminal PrepareTerminal(StateDatabase database, AcceptedExecutionAdmission accepted,
        ResourceLeaseManager resources, TimeProvider clock)
    {
        Require(database);
        return PreparedBudgetTerminal.Capture(lease, database, accepted, resources, clock);
    }

    internal void RequireMeasured(AcceptedExecutionAdmission accepted)
    {
        var prepared = accepted.Prepared; prepared.RequireLive();
        var runId = Guid.Parse(prepared.Total.Plan.ReadWire().RunId);
        lock (_gate)
            if (!_clocks.TryGetValue(runId, out var measured) || !ReferenceEquals(measured.Budget, prepared.TimeBudget))
                throw RunBudgetRows.Invalid();
    }

    internal ExecutionBudgetSnapshot ReadForRetry(StateDatabase database, AttemptReceipt receipt, TimeProvider clock)
    {
        Require(database);
        if (new AttemptReceiptStore(database).Read(receipt.Facts.AttemptId) != receipt) throw RunBudgetRows.Invalid();
        var saved = Read(database, receipt.Facts.RunId) ?? throw RunBudgetRows.Invalid();
        if (saved.JournalId != receipt.JournalId || saved.TerminalReceiptDigest != receipt.SourceReceiptDigest ||
            saved.ClockRegressed || clock.GetUtcNow() < saved.ObservedAtUtc)
            throw new IOException("execution_retry_budget_unverified");
        lock (_gate)
        {
            if (!_clocks.TryGetValue(receipt.Facts.RunId, out var measured))
                throw new IOException("execution_retry_budget_continuity_unknown");
            return measured.Budget.Read();
        }
    }

    internal ExecutionBudgetSnapshot? ReadWaitingBudget(StateDatabase database, Guid runId)
    {
        Require(database);
        lock (_gate) return _clocks.TryGetValue(runId, out var measured) ? measured.Budget.Read() : null;
    }

    internal ExecutionTimeBudget CounterForRetry(StateDatabase database, AttemptReceipt receipt)
    {
        _ = ReadForRetry(database, receipt, TimeProvider.System);
        lock (_gate) return _clocks.TryGetValue(receipt.Facts.RunId, out var measured) ? measured.Budget :
            throw new IOException("execution_retry_budget_continuity_unknown");
    }

    internal void ReleaseBlockedOccurrence(StateDatabase database, NexusPipeline.Core.Scheduling.ScheduledOccurrence occurrence)
    {
        Require(database);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var actual = ScheduleMinuteRows.ReadOccurrence(connection, transaction, occurrence.Frozen.OccurrenceId, lease.Instance.InstanceId);
        if (actual != occurrence || actual.Phase != NexusPipeline.Core.Scheduling.ScheduleOccurrencePhase.Blocked)
            throw RunBudgetRows.Invalid();
        Remove(actual.Frozen.RunId);
    }

    internal void ReleaseBlockedStartup(StateDatabase database, Guid occurrenceId)
    {
        Require(database);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var actual = StartupQueueRows.Read(connection, transaction, occurrenceId, lease.Instance.InstanceId);
        if (actual.BlockReason is null) throw RunBudgetRows.Invalid();
        Remove(actual.Occurrence.RunId);
    }

    internal AcceptedExecutionRun Continuation(StateDatabase database, AttemptReceipt receipt)
    {
        _ = ReadForRetry(database, receipt, TimeProvider.System);
        lock (_gate)
        {
            var token = _clocks[receipt.Facts.RunId].AcceptedRun;
            return token is { IsActive: true } ? token : throw new IOException("execution_run_inactive");
        }
    }

    internal AcceptedExecutionRun AcceptedForTerminal(StateDatabase database, AttemptReceipt receipt)
    {
        Require(database);
        if (new AttemptReceiptStore(database).Read(receipt.Facts.AttemptId) != receipt)
            throw RunBudgetRows.Invalid();
        lock (_gate)
        {
            var token = _clocks.TryGetValue(receipt.Facts.RunId, out var measured) ? measured.AcceptedRun : null;
            return token is { IsActive: true } ? token : throw new IOException("execution_run_inactive");
        }
    }

    internal void ReleaseFinal(StateDatabase database, AttemptReceipt receipt, ExecutionRunFinality finality)
    {
        Require(database);
        finality.RequireDatabase(database);
        if (!finality.IsFinal(receipt)) throw new IOException("execution_run_not_final");
        Remove(receipt.Facts.RunId);
    }

    private void Remove(Guid runId)
    {
        MeasuredRun? removed;
        lock (_gate) _clocks.Remove(runId, out removed);
        removed?.AcceptedRun?.Dispose();
    }

    private ExecutionTimeBudget GetOrCreate(Guid runId, Guid operationId, TimeSpan total, DateTimeOffset deadline,
        RunBudgetObservation? saved, TimeProvider clock, long startedTimestamp, DateTimeOffset firstPreparedAt)
    {
        lock (_gate)
        {
            if (_clocks.TryGetValue(runId, out var current))
            {
                if (current.OperationId != operationId || current.Total != total || current.Deadline != deadline) throw RunBudgetRows.Invalid();
                return current.Budget;
            }
            if (_clocks.Count >= MaximumMeasuredRuns) throw new IOException("execution_budget_capacity");
            // A new owner cannot measure the uncheckpointed tail of the previous process.
            if (saved is not null) throw new IOException("execution_budget_continuity_unknown");
            var budget = new ExecutionTimeBudget(clock, startedTimestamp, total, deadline);
            _clocks.Add(runId, new(budget, operationId, total, deadline)); return budget;
        }
    }

    private static RunBudgetObservation? Read(StateDatabase database, Guid runId)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        return RunBudgetRows.Read(connection, transaction, runId);
    }

    private static void Append(StateDatabase database, Guid runId, string subject, ExecutionTimeBudget budget,
        DateTimeOffset observed, Guid journal, RunBudgetObservation? saved, TimeSpan total, DateTimeOffset deadline)
    {
        var value = budget.Read();
        var next = new RunBudgetObservation("nexus.execution.run-budget", 1, runId,
            saved?.StartOperationId ?? throw RunBudgetRows.Invalid(), saved.OccurrenceId, subject,
            checked((long)total.TotalMilliseconds), saved.FirstPreparedAtUtc, deadline, value.ConsumedMilliseconds, observed,
            saved.ClockRegressed || observed < saved.ObservedAtUtc, saved.Ordinal + 1, RunBudgetRows.Identity(saved).OperationId, journal);
        database.Write((connection, transaction) => { RunBudgetRows.Append(connection, transaction, next); return true; });
    }

    internal void Close()
    {
        MeasuredRun[] held;
        lock (_gate) { held = _clocks.Values.ToArray(); _clocks.Clear(); }
        foreach (var item in held) item.AcceptedRun?.Dispose();
    }

    private void Require(StateDatabase database)
    {
        lease.VerifyHeld(lease.Instance);
        if (database.InstanceId != lease.Instance.InstanceId || database.RuntimeRoot != lease.Instance.RuntimeRoot ||
            !StringComparer.OrdinalIgnoreCase.Equals(database.DatabasePath, Path.Combine(lease.Instance.RuntimeRoot, "state", "nexus.db")))
            throw RunBudgetRows.Invalid();
    }
    private sealed record MeasuredRun(ExecutionTimeBudget Budget, Guid OperationId, TimeSpan Total, DateTimeOffset Deadline)
    {
        internal AcceptedExecutionRun? AcceptedRun { get; set; }
    }
}
