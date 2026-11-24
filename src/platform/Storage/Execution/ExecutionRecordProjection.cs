using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Globalization;
using System.Runtime.Versioning;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
public sealed class ExecutionRecordProjection(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    InstanceLease lease, ExecutionRunFinality finality) : IAttemptTerminalObserver
{
    public void OnCommitted(AttemptReceipt receipt)
    {
        lease.VerifyHeld(instance);
        finality.RequireDatabase(database);
        if (IsInitialization(receipt)) return;
        new LogCatalogProjection(instance, database, contracts, lease).OnCommitted(receipt);
        new ImageCatalogProjection(instance, database, contracts, lease).OnCommitted(receipt);
        if (!finality.IsFinal(receipt)) return;
        Publish(receipt, new ExecutionRecordWitness(instance, database, contracts).Read(receipt));
    }

    public void ReconcileStopped(AttemptReceipt receipt)
    {
        lease.VerifyHeld(instance);
        finality.RequireDatabase(database);
        if (finality.IsFinal(receipt)) { OnCommitted(receipt); return; }
        if (IsInitialization(receipt)) return;
        new LogCatalogProjection(instance, database, contracts, lease).OnCommitted(receipt);
        new ImageCatalogProjection(instance, database, contracts, lease).OnCommitted(receipt);
        var timing = RecoveredRecordTiming.Capture(instance, database, contracts, receipt);
        Publish(receipt, new ExecutionRecordWitness(instance, database, contracts).Read(receipt, timing));
    }

    public Guid? ReconcileStoppedPage(Guid? after = null, int maximum = 64)
    {
        lease.VerifyHeld(instance);
        if (maximum is < 1 or > 200 || after == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(maximum));
        var attempts = new List<Guid>();
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT a.attempt_id FROM records.attempt_receipts a JOIN main.execution_start_sources s ON s.operation_id=a.operation_id " +
                "LEFT JOIN records.run_records r ON r.run_id=a.run_id WHERE a.attempt_id>$after AND r.run_id IS NULL " +
                "AND NOT EXISTS(SELECT 1 FROM main.execution_start_sources n WHERE n.run_id=a.run_id AND n.attempt_ordinal>a.ordinal) " +
                "AND NOT EXISTS(SELECT 1 FROM main.execution_retry_intents i LEFT JOIN main.execution_retry_outcomes d ON d.retry_operation_id=i.operation_id " +
                "WHERE i.previous_attempt_id=a.attempt_id AND d.operation_id IS NULL) ORDER BY a.attempt_id LIMIT $limit;";
            command.Parameters.AddWithValue("$after", after?.ToString("D") ?? "");
            command.Parameters.AddWithValue("$limit", maximum);
            using var rows = command.ExecuteReader();
            while (rows.Read()) attempts.Add(Guid.ParseExact(rows.GetString(0), "D"));
        }
        foreach (var id in attempts)
            ReconcileStopped(new AttemptReceiptStore(database).Read(id) ?? throw new IOException("record_recovery_receipt_missing"));
        return attempts.Count == maximum ? attempts[^1] : null;
    }

    private bool IsInitialization(AttemptReceipt receipt)
    {
        var bound = AttemptPreparedBinding.Read(instance, contracts, database, receipt.JournalId);
        if (bound.Plan.OperationId != receipt.OperationId.ToString("D") ||
            bound.Plan.AttemptId != receipt.Facts.AttemptId.ToString("D") ||
            new AttemptReceiptStore(database).Read(receipt.Facts.AttemptId) != receipt)
            throw new IOException("record_execution_source_unbound");
        return ProjectInitializationPlan.IsInitialization(bound.Total);
    }

    private void Publish(AttemptReceipt receipt, CompletedRunRecord record)
    {
        var bindings = BindingRecordProjection.Capture(instance, database, contracts, receipt.Facts.RunId, receipt.Facts.AttemptId);
        lease.PublishExecution(() =>
        {
            var images = new ImageRecordAssets(instance, database, contracts); images.Capture(receipt);
            database.Write((connection, transaction) =>
            {
                ExecutionRecordWitness.RequireFinalInside(connection, transaction, receipt);
                RunRecordStore.PublishInside(connection, transaction, record);
                BindingRecordProjection.Publish(connection, transaction, receipt.Facts.RunId, bindings);
                AccountRecordProjection.Publish(connection, transaction, record, bindings);
                images.PublishInside(connection, transaction, receipt);
                return true;
            });
        });
    }

    internal static bool RequireRecoveredRecord(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, AttemptReceipt receipt)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT end_time_kind FROM records.run_records WHERE run_id=$run;";
        command.Parameters.AddWithValue("$run", receipt.Facts.RunId.ToString("D"));
        if (command.ExecuteScalar() is not long kind || kind != (int)EndTimeKind.ReconciledTerminalObservation) return false;
        var timing = RecoveredRecordTiming.Capture(instance, database, contracts, receipt);
        var expected = new ExecutionRecordWitness(instance, database, contracts).Read(receipt, timing);
        using var transaction = DatabaseConnections.BeginRead(connection);
        RunRecordStore.RequirePublishedInside(connection, transaction, expected);
        return true;
    }
}

internal static class AccountRecordProjection
{
    internal static void Publish(SqliteConnection connection, SqliteTransaction transaction,
        CompletedRunRecord run, IReadOnlyList<ExecutionStepView> steps)
    {
        foreach (var account in steps.Where(step => step.UserId is not null).GroupBy(step => step.UserId))
        {
            var facts = Facts(run.FinalAttempt, account);
            var engine = facts.Engine; var business = facts.Business; var lifecycle = facts.Lifecycle;
            (run with { UserId = Guid.Parse(account.Key!), FinalAttempt = facts }).Validate();
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO records.account_run_records(ended_at_utc_ticks,run_id,user_id,engine_outcome,business_outcome,lifecycle_outcome,reason_code) " +
                "VALUES($ended,$run,$user,$engine,$business,$lifecycle,$reason);";
            command.Parameters.AddWithValue("$ended", run.EndedAt.UtcTicks);
            command.Parameters.AddWithValue("$run", run.FinalAttempt.RunId.ToString("D"));
            command.Parameters.AddWithValue("$user", account.Key!);
            command.Parameters.AddWithValue("$engine", (int)engine);
            command.Parameters.AddWithValue("$business", (int)business);
            command.Parameters.AddWithValue("$lifecycle", (int)lifecycle);
            command.Parameters.AddWithValue("$reason", facts.ReasonCode);
            command.ExecuteNonQuery();
        }
    }

    internal static AttemptFacts Facts(AttemptFacts run, IEnumerable<ExecutionStepView> steps)
    {
        var selected = steps.OrderBy(step => Rank(step.Business)).ThenBy(step => step.StepId, StringComparer.Ordinal).First();
        var collected = selected.Phase == "collected";
        var engine = collected && Enum.TryParse<EngineOutcome>(selected.Engine, out var rawEngine) ? rawEngine : EngineOutcome.NotStarted;
        var business = collected && Enum.TryParse<BusinessOutcome>(selected.Business, out var rawBusiness) ? rawBusiness : BusinessOutcome.Unverified;
        var lifecycle = business == BusinessOutcome.Cancelled || business == BusinessOutcome.Unverified && run.Lifecycle == LifecycleOutcome.Cancelled
            ? LifecycleOutcome.Cancelled : business == BusinessOutcome.Unverified && run.Lifecycle == LifecycleOutcome.Interrupted ? LifecycleOutcome.Interrupted :
                collected ? LifecycleOutcome.Completed : LifecycleOutcome.Skipped;
        return run with { Engine = engine, Business = business, Lifecycle = lifecycle,
            ReasonCode = lifecycle is LifecycleOutcome.Cancelled or LifecycleOutcome.Interrupted ? run.ReasonCode : selected.ReasonCode ?? run.ReasonCode };
    }

    private static int Rank(string? business) => business switch
    { "Failed" => 0, "Partial" => 1, "Cancelled" => 2, null or "Unverified" => 3, "Succeeded" => 5, _ => 4 };
}

internal static class BindingRecordProjection
{
    internal static IReadOnlyList<ExecutionStepView> Capture(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, Guid runId, Guid attemptId)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM records.run_records WHERE run_id=$run);";
        command.Parameters.AddWithValue("$run", runId.ToString("D"));
        if ((long)command.ExecuteScalar()! != 0) return [];
        var reports = new ExecutionLogReader(instance, database, contracts);
        var final = reports.ReadReport(runId, attemptId).Steps;
        var selected = final.Where(step => step.BindingId is not null).ToDictionary(step => step.StepId, StringComparer.Ordinal);
        if (selected.Values.All(step => step.Phase == "collected")) return selected.Values.ToArray();
        command.CommandText = "SELECT attempt_id FROM records.attempt_receipts WHERE run_id=$run AND ordinal < " +
            "(SELECT ordinal FROM records.attempt_receipts WHERE attempt_id=$attempt) ORDER BY ordinal DESC LIMIT 99;";
        command.Parameters.AddWithValue("$attempt", attemptId.ToString("D"));
        var earlier = new List<Guid>();
        using (var rows = command.ExecuteReader()) while (rows.Read()) earlier.Add(Guid.Parse(rows.GetString(0)));
        foreach (var previous in earlier)
        {
            foreach (var step in reports.ReadReport(runId, previous).Steps.Where(step => step.BindingId is not null && step.Phase == "collected"))
                if (selected.TryGetValue(step.StepId, out var current) && current.Phase != "collected" &&
                    step.UserId == current.UserId && step.ScriptId == current.ScriptId) selected[step.StepId] = step;
            if (selected.Values.All(step => step.Phase == "collected")) break;
        }
        return selected.Values.ToArray();
    }

    internal static void Publish(SqliteConnection connection, SqliteTransaction transaction, Guid runId,
        IReadOnlyList<ExecutionStepView> steps)
    {
        foreach (var group in steps.Where(step => step.BindingId is not null).GroupBy(step => step.BindingId))
        {
            var step = group.OrderBy(item => Rank(Tone(item))).ThenBy(item => item.StepId, StringComparer.Ordinal).First();
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO records.binding_task_records(run_id,binding_id,user_id,script_id,tone,reason) " +
                "VALUES($run,$binding,$user,$script,$tone,$reason);";
            command.Parameters.AddWithValue("$run", runId.ToString("D"));
            command.Parameters.AddWithValue("$binding", Guid.Parse(step.BindingId!).ToString("D"));
            command.Parameters.AddWithValue("$user", Guid.Parse(step.UserId!).ToString("D"));
            command.Parameters.AddWithValue("$script", Guid.Parse(step.ScriptId).ToString("D"));
            command.Parameters.AddWithValue("$tone", Tone(step));
            command.Parameters.AddWithValue("$reason", step.Phase == "collected" && step.Tasks.Count > 0 ? "latest" : "no_task_report");
            command.ExecuteNonQuery();
        }
    }

    private static string Tone(ExecutionStepView step) => step.Business switch
    {
        "Failed" => "bad", "Partial" => "warn", "Succeeded" => "ok", _ => "muted"
    };
    private static int Rank(string tone) => tone switch { "bad" => 0, "warn" => 1, "muted" => 2, _ => 3 };
}

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
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var transaction = DatabaseConnections.BeginRead(connection);
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

[SupportedOSPlatform("windows")]
internal sealed class ExecutionRecordWitness(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal CompletedRunRecord Read(AttemptReceipt receipt, RecoveredRecordTiming? recovery = null)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records);
        using var transaction = DatabaseConnections.BeginRead(connection);
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
            command.CommandText = "SELECT o.created_at FROM main.execution_retry_outcomes d JOIN main.operations o ON o.operation_id=d.operation_id WHERE d.retry_operation_id=$retry;";
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
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.execution_start_sources WHERE run_id=$run AND attempt_ordinal>$ordinal);";
        command.Parameters.AddWithValue("$run", receipt.Facts.RunId.ToString("D"));
        command.Parameters.AddWithValue("$ordinal", receipt.Facts.Ordinal);
        if ((long)command.ExecuteScalar()! != 0) throw Invalid();
        var next = ExecutionRetryRows.After(connection, transaction, receipt.Facts.AttemptId);
        if (next is not null && ExecutionRetryOutcomes.ReadReason(connection, transaction, next) is null) throw Invalid();
    }

    private static IOException Invalid() => new("record_execution_source_unbound");
}
