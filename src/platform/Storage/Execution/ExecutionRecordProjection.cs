using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

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
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT a.attempt_id FROM attempt_receipts a JOIN execution_start_sources s ON s.operation_id=a.operation_id " +
                "LEFT JOIN run_records r ON r.run_id=a.run_id WHERE a.attempt_id>$after AND r.run_id IS NULL " +
                "AND NOT EXISTS(SELECT 1 FROM execution_start_sources n WHERE n.run_id=a.run_id AND n.attempt_ordinal>a.ordinal) " +
                "AND NOT EXISTS(SELECT 1 FROM execution_retry_intents i LEFT JOIN execution_retry_outcomes d ON d.retry_operation_id=i.operation_id " +
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
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT end_time_kind FROM run_records WHERE run_id=$run;";
        command.Parameters.AddWithValue("$run", receipt.Facts.RunId.ToString("D"));
        if (command.ExecuteScalar() is not long kind || kind != (int)EndTimeKind.ReconciledTerminalObservation) return false;
        var timing = RecoveredRecordTiming.Capture(instance, database, contracts, receipt);
        var expected = new ExecutionRecordWitness(instance, database, contracts).Read(receipt, timing);
        using var transaction = connection.BeginTransaction(deferred: true);
        RunRecordStore.RequirePublishedInside(connection, transaction, expected);
        return true;
    }
}
