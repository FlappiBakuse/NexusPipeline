using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

[SupportedOSPlatform("windows")]
public sealed class ExecutionNotificationRegistration : IAttemptTerminalObserver
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly InstanceLease _lease;
    private readonly ExecutionRunFinality _finality;

    public ExecutionNotificationRegistration(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, ExecutionRunFinality finality)
    {
        lease.VerifyHeld(instance);
        if (instance.InstanceId != database.InstanceId || database.RuntimeRoot != instance.RuntimeRoot ||
            !StringComparer.OrdinalIgnoreCase.Equals(database.DatabasePath, Path.Combine(instance.RuntimeRoot, "state", "runtime.db")))
            throw NotificationReceiptCodec.Invalid();
        finality.RequireDatabase(database);
        _instance = instance; _database = database; _contracts = contracts; _lease = lease; _finality = finality;
    }

    public void OnCommitted(AttemptReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        _lease.VerifyHeld(_instance);
        if (Existing(receipt)) return;
        var source = ExecutionNotificationSource.ReadActual(_instance, _database, _contracts, _finality, receipt);
        if (source is null) return;
        for (var count = 0; count < 3; count++)
        {
            if (Existing(receipt)) return;
            try
            {
                using var prepared = PreparedNotificationReceipt.Capture(_instance, _database, _contracts, source);
                var accepted = prepared.Accept(_database, _lease);
                using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Notifications | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
                using var transaction = connection.BeginTransaction(deferred: true);
                var saved = new NotificationReceiptCodec(_instance, _database, _contracts).Read(accepted, connection, transaction);
                if (saved.Document.Receipt != receipt) throw NotificationReceiptCodec.Invalid();
                transaction.Commit();
                return;
            }
            catch (Exception error) when (count < 2 &&
                (error is NotificationTaskException { Message: "notification_revision_conflict" } ||
                 error is InvalidOperationException { Message: "notification_revision_conflict" })) { }
        }
        throw new IOException("notification_registration_conflict");
    }

    internal bool HasPending()
    {
        _lease.VerifyHeld(_instance);
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records | DatabaseDomain.Notifications);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM records.run_records r LEFT JOIN notifications.notification_receipt_sources s " +
            "ON s.attempt_id=r.attempt_id WHERE s.attempt_id IS NULL);";
        return (long)command.ExecuteScalar()! != 0;
    }

    public int ReconcilePending(int limit = 64, CancellationToken cancellation = default)
    {
        _lease.VerifyHeld(_instance);
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        var attempts = new List<Guid>();
        using (var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Notifications | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT r.attempt_id FROM records.run_records r LEFT JOIN notifications.notification_receipt_sources s ON s.attempt_id=r.attempt_id " +
                "WHERE s.attempt_id IS NULL ORDER BY r.run_id LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", limit);
            using var row = command.ExecuteReader();
            while (row.Read()) attempts.Add(Guid.ParseExact(row.GetString(0), "D"));
        }
        foreach (var attempt in attempts)
        {
            cancellation.ThrowIfCancellationRequested();
            var receipt = new AttemptReceiptStore(_database).Read(attempt) ?? throw NotificationReceiptCodec.Invalid();
            using var reads = new NotificationAttemptReads(_instance, _database, _contracts, _lease);
            reads.HoldJournal(receipt.JournalId);
            OnCommitted(receipt);
        }
        return attempts.Count;
    }

    private bool Existing(AttemptReceipt receipt)
    {
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records | DatabaseDomain.Notifications | DatabaseDomain.System);
        using var transaction = connection.BeginTransaction(deferred: true);
        var reference = NotificationReceiptRows.Find(connection, transaction, receipt.Facts.AttemptId);
        if (reference is null) return false;
        var saved = new NotificationReceiptCodec(_instance, _database, _contracts).Read(reference, connection, transaction);
        if (saved.Document.Receipt != receipt) throw NotificationReceiptCodec.Invalid();
        transaction.Commit();
        return true;
    }
}
