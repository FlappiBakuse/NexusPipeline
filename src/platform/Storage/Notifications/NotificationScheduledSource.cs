using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal static class NotificationScheduledSource
{
    internal static NotificationWorkSnapshot VerifyCommitted(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, BlobReference reference, Guid expectedRunId)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var source = new NotificationScheduledCodec(instance, contracts).Read(reference, connection, transaction);
        var invocation = new ScheduledProviderEvidence(instance, contracts).Read(source.Document.Invocation, connection, transaction);
        if (expectedRunId == Guid.Empty || invocation.Source.Document.Invocation.RunId != expectedRunId)
            throw NotificationScheduledCodec.Invalid();
        NotificationWorkFanout.Verify(instance, database, contracts, source, connection, transaction, "notification_scheduled_deliveries");
        transaction.Commit(); return source;
    }
}
