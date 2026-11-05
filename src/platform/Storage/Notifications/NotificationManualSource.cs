using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal static class NotificationManualSource
{
    internal static void VerifyCommitted(RuntimeInstance instance, StateDatabase database, ContractReader contracts, BlobReference reference, Guid expectedRunId)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var source = new NotificationManualCodec(instance, contracts).Read(reference, connection, transaction);
        var invocation = ManualWorkRows.Read(instance, contracts, connection, transaction, source.Document.Invocation);
        if (expectedRunId == Guid.Empty || invocation.Source.Document.Invocation.RunId != expectedRunId) throw NotificationManualCodec.Invalid();
        NotificationWorkFanout.Verify(instance, database, contracts, source, connection, transaction, "notification_manual_deliveries"); transaction.Commit();
    }
}
