using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
public sealed class ExecutionRetryService(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    InstanceLease lease, ExecutionAdmissionService admission, IAttemptTerminalObserver? terminalObserver = null) : IExecutionRetryStore, IExecutionRetryAdmission
{
    private readonly ExecutionRunFinality _finality = new(instance, database, contracts);
    private readonly object _waitingGate = new();
    private DatabaseVersion? _waitingRevision;
    private IReadOnlyList<ExecutionRetryIntent> _waiting = [];

    public IPreparedExecutionRetry? Prepare(Guid runId, Guid previousAttemptId, DateTimeOffset now)
    {
        lease.VerifyHeld(instance);
        var value = ReadAfter(previousAttemptId);
        if (value is null) return null;
        if (value.Frozen.RunId != runId || now.Offset != TimeSpan.Zero) throw RetryIntentCodec.Invalid();
        return PreparedExecutionRetry.Capture(instance, database, contracts, lease, RetryIntentCodec.Identity(value).OperationId);
    }

    public IPreparedExecutionRetry? ReadWaiting(Guid runId)
    {
        lease.VerifyHeld(instance);
        foreach (var value in Waiting())
        {
            var id = RetryIntentCodec.Identity(value).OperationId;
            if (value.Frozen.RunId == runId)
                return PreparedExecutionRetry.Capture(instance, database, contracts, lease, id);
        }
        return null;
    }

    public IExecutionAdmissionReceipt Start(IPreparedExecutionRetry retry, DateTimeOffset now)
    {
        if (retry is not PreparedExecutionRetry actual) throw RetryIntentCodec.Invalid();
        lease.VerifyHeld(instance);
        return admission.StartRetry(actual, now);
    }

    public void Tick(DateTimeOffset now)
    {
        lease.VerifyHeld(instance);
        if (now.Offset != TimeSpan.Zero) throw RetryIntentCodec.Invalid();
        foreach (var value in Waiting())
        {
            var id = RetryIntentCodec.Identity(value).OperationId;
            try
            {
                var budget = lease.RunBudgets.ReadWaitingBudget(database, value.Frozen.RunId);
                if (budget is { IsExhausted: false } && now < value.Frozen.NotBefore && now < value.Frozen.DeadlineUtc) continue;
                using var prepared = PreparedExecutionRetry.Capture(instance, database, contracts, lease, id);
                if (prepared.TimeBudget.Read().IsExhausted || now >= value.Frozen.DeadlineUtc)
                { Decline(value, "retry_budget_exhausted", now); continue; }
                _ = admission.StartRetry(prepared, now);
            }
            catch (ExecutionSelectionException failure) when (failure.Message is "execution_resource_conflict" or "execution_host_unavailable")
            { }
            catch (ConfigurationException failure) when (failure.Message is "configuration_revision_conflict" or
                "configuration_binding_revision_conflict" or "configuration_binding_missing")
            { Decline(value, "retry_sources_unavailable", now); }
            catch (IOException failure) when (failure.Message is "execution_retry_declined" or "execution_retry_already_accepted")
            { }
            catch (IOException failure) when (failure.Message is "execution_provider_activation_revoked" or
                "execution_extension_provider_unavailable" or "execution_provider_activation_changed" or "execution_provider_lock_changed")
            { Decline(value, "retry_authority_revoked", now); }
            catch (IOException failure) when (failure.Message is "execution_image_changed" or
                "execution_directory_changed" or "execution_physical_claims_changed")
            { Decline(value, "retry_sources_unavailable", now); }
            catch (IOException failure) when (failure.Message is "execution_retry_budget_continuity_unknown" or
                "execution_budget_continuity_unknown" or "execution_retry_budget_unverified")
            { Decline(value, "retry_budget_unknown", now); }
        }
    }

    public async Task RunAsync(CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!stopping.IsCancellationRequested)
        {
            Tick(TimeProvider.System.GetUtcNow());
            try { if (!await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false)) return; }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
        }
    }

    public NexusPipeline.Core.Operations.OperationAcceptance CancelWaiting(Guid operationId, string subject,
        Guid startOperationId, Guid runId, Guid attemptId, DateTimeOffset now)
    {
        lease.VerifyHeld(instance);
        var result = lease.PublishExecution(() => ExecutionRetryCancellation.Commit(database, operationId, subject,
            startOperationId, runId, attemptId, now));
        ReleaseIfFinal(runId);
        return result;
    }

    public void CancelWaiting(Guid runId, string subject, DateTimeOffset now)
    {
        lease.VerifyHeld(instance);
        lease.PublishExecution(() =>
        {
            using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
            using var transaction = connection.BeginTransaction(deferred: true);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT operation_id FROM main.execution_retry_intents WHERE run_id=$run ORDER BY next_ordinal DESC LIMIT 1;";
            command.Parameters.AddWithValue("$run", runId.ToString("D"));
            var id = command.ExecuteScalar() as string ?? throw new IOException("execution_retry_not_waiting");
            var found = ExecutionRetryRows.Read(connection, transaction, Guid.ParseExact(id, "D"));
            if (found.SubjectId != subject) throw new IOException("execution_retry_subject_mismatch");
            WriteDecline(found, "retry_run_cancelled", now);
            return true;
        });
        ReleaseIfFinal(runId);
    }

    private void Decline(ExecutionRetryIntent value, string reason, DateTimeOffset now)
    {
        lease.PublishExecution(() => { WriteDecline(value, reason, now); return true; });
        ReleaseIfFinal(value.Frozen.RunId);
    }

    private void WriteDecline(ExecutionRetryIntent value, string reason, DateTimeOffset now)
    {
        lease.VerifyHeld(instance);
        if (now.Offset != TimeSpan.Zero) throw RetryIntentCodec.Invalid();
        database.Write((connection, transaction) =>
        { ExecutionRetryOutcomes.DeclineInside(connection, transaction, value, reason, now); return true; });
    }

    public void ReleaseIfFinal(Guid runId)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT attempt_id FROM records.attempt_receipts WHERE run_id=$run ORDER BY ordinal DESC LIMIT 1;";
        command.Parameters.AddWithValue("$run", runId.ToString("D"));
        if (command.ExecuteScalar() is not string id) return;
        var receipt = new AttemptReceiptStore(database).Read(Guid.ParseExact(id, "D")) ?? throw RetryIntentCodec.Invalid();
        if (_finality.IsFinal(receipt))
        {
            terminalObserver?.OnCommitted(receipt);
            lease.RunBudgets.ReleaseFinal(database, receipt, _finality);
        }
    }

    private IReadOnlyList<ExecutionRetryIntent> Waiting()
    {
        lock (_waitingGate)
        {
            var revision = DatabaseVersion.Read(database, null, DatabaseDomain.Runtime | DatabaseDomain.Records);
            if (_waitingRevision == revision) return _waiting;
            using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records);
            using var transaction = connection.BeginTransaction(deferred: true);
            var values = ExecutionRetryRows.Waiting(connection, transaction)
                .Select(id => ExecutionRetryRows.Read(connection, transaction, id)).ToArray();
            // These bounded candidates carry no admission authority; due retries acquire fresh preparation.
            if (revision == DatabaseVersion.Read(database, null, DatabaseDomain.Runtime | DatabaseDomain.Records))
            { _waiting = values; _waitingRevision = revision; }
            return values;
        }
    }

    private ExecutionRetryIntent? ReadAfter(Guid attempt)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records);
        using var transaction = connection.BeginTransaction(deferred: true);
        return ExecutionRetryRows.After(connection, transaction, attempt);
    }
}
