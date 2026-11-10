using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal sealed class NotificationManualCodec(RuntimeInstance instance, ContractReader contracts)
{
    internal const string Format = "nexus.notification.manual-source";
    internal const string Kind = "notification.manual-source";
    internal const string Subject = "host:manual-provider";
    internal NotificationWorkSnapshot Persist(NotificationWorkDocument document)
    {
        NotificationWorkShape.Require(document, Format, instance.InstanceId);
        return new(document, new ImmutableBlobStore(instance, contracts).PersistPlan(NotificationCatalogCodec.Encode(document)));
    }

    internal NotificationWorkSnapshot Read(BlobReference reference, SqliteConnection connection, SqliteTransaction transaction, Guid? expectedRunId = null)
    {
        var document = new NotificationCatalogCodec(instance, contracts).ReadDocument<NotificationWorkDocument>(reference);
        NotificationWorkShape.Require(document, Format, instance.InstanceId);
        var source = new NotificationWorkSnapshot(document, reference); NotificationManualRows.RequireSaved(connection, transaction, source);
        var invocation = WorkInvocationEvidence.ReadManual(instance, contracts, document.Invocation, connection, transaction);
        if (!invocation.Binds(document.Begin, document.OperationId, document.AcceptedConsumedMilliseconds, document.Targets.Select(item => item.TaskId)) ||
            expectedRunId is { } run && (run == Guid.Empty || invocation.RunId != run))
            throw Invalid();
        NotificationManualRows.RequireOrigin(connection, transaction, document.OperationId, invocation.OperationId);
        NotificationWorkShape.RequireCatalog(instance, contracts, document, connection, transaction); return source;
    }

    internal NotificationDeliveryDocument Delivery(NotificationWorkSnapshot source, NotificationWorkTarget target) =>
        NotificationWorkShape.Delivery(instance.InstanceId, source, target, Subject, "manualInvocation");
    internal static OperationIdentity Identity(NotificationWorkSnapshot source) => new(source.Document.OperationId, Subject, source.Reference.Sha256, Kind);
    internal static IOException Invalid() => new("notification_manual_source_unbound");
}
