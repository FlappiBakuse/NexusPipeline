using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal static class NotificationScheduledSource
{
    internal static void VerifyCommitted(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, BlobReference reference, Guid expectedRunId)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records | DatabaseDomain.Notifications | DatabaseDomain.System);
        using var transaction = connection.BeginTransaction(deferred: true);
        var source = new NotificationScheduledCodec(instance, contracts).Read(reference, connection, transaction, expectedRunId);
        NotificationWorkFanout.Verify(instance, database, contracts, source, connection, transaction, "notification_scheduled_deliveries");
        transaction.Commit();
    }
}
