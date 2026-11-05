using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal sealed class NotificationScheduledCodec(RuntimeInstance instance, ContractReader contracts)
{
    internal const string Format = "nexus.notification.scheduled-source";
    internal const string Kind = "notification.scheduled-source";
    internal const string Subject = "host:scheduled-provider";

    internal NotificationWorkSnapshot Persist(NotificationWorkDocument document)
    {
        RequireShape(document);
        return new(document, new ImmutableBlobStore(instance, contracts).PersistPlan(NotificationCatalogCodec.Encode(document)));
    }

    internal NotificationWorkSnapshot Read(BlobReference reference, SqliteConnection connection, SqliteTransaction transaction)
    {
        var document = new NotificationCatalogCodec(instance, contracts).ReadDocument<NotificationWorkDocument>(reference);
        RequireShape(document);
        var source = new NotificationWorkSnapshot(document, reference);
        NotificationScheduledRows.RequireSaved(connection, transaction, source);
        var invocation = new ScheduledProviderEvidence(instance, contracts).Read(document.Invocation, connection, transaction);
        if (invocation.BeginReference != document.Begin || invocation.Begin is null ||
            invocation.Source.Document.Invocation.NotificationOperationId != document.OperationId ||
            document.AcceptedConsumedMilliseconds < invocation.Begin.ConsumedMilliseconds ||
            document.AcceptedConsumedMilliseconds > invocation.Source.Document.Task.TimeoutSeconds * 1000L ||
            document.Targets.Any(item => !invocation.Source.Document.Invocation.NotificationTaskIds.Contains(item.TaskId)))
            throw Invalid();
        NotificationScheduledRows.RequireOccurrence(connection, transaction, document.OperationId, invocation.Source.Document.OperationId);
        NotificationWorkShape.RequireCatalog(instance, contracts, document, connection, transaction);
        return source;
    }

    internal NotificationDeliveryDocument Delivery(NotificationWorkSnapshot source, NotificationWorkTarget target) =>
        NotificationWorkShape.Delivery(instance.InstanceId, source, target, Subject, "scheduledInvocation");

    internal static OperationIdentity Identity(NotificationWorkSnapshot source) =>
        new(source.Document.OperationId, Subject, source.Reference.Sha256, Kind);
    internal static Guid DeliveryId(Guid operation, Guid task) => ReceiptRouteCodec.DeliveryId(operation, 0, task);
    internal static string RequestDigest(BlobReference invocation, IReadOnlyList<Guid> ids, string title, string body) =>
        Convert.ToHexStringLower(SHA256.HashData(NotificationCatalogCodec.Encode(new { invocation, ids, title, body })));

    private void RequireShape(NotificationWorkDocument document) => NotificationWorkShape.Require(document, Format, instance.InstanceId);

    internal static IOException Invalid() => new("notification_scheduled_source_unbound");
}
