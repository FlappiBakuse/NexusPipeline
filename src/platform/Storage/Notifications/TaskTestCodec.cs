using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal sealed class TaskTestCodec(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal const string Format = "nexus.notification.saved-task-test";
    internal const string OperationKind = "notification.saved-test";
    internal const string Title = "NexusPipeline saved notification test";
    internal const string Body = "NexusPipeline is testing this saved notification task. No execution result is changed.";

    internal static OperationIdentity Identity(Guid operation, string subject, long catalogRevision, Guid task, long taskRevision)
    {
        if (operation == Guid.Empty || task == Guid.Empty || catalogRevision < 1 || taskRevision < 1 ||
            catalogRevision > 9007199254740991 || taskRevision > 9007199254740991 ||
            string.IsNullOrWhiteSpace(subject) || subject.Contains('\0') || Encoding.UTF8.GetByteCount(subject) > 1024)
            throw Invalid();
        var digest = SHA256.HashData(NotificationCatalogCodec.Encode(new { catalogRevision, task, taskRevision }));
        return new(operation, subject, Convert.ToHexStringLower(digest), OperationKind);
    }

    internal static OperationIdentity Identity(TaskTestDocument source) =>
        Identity(source.OperationId, source.SubjectId, source.ExpectedCatalogRevision, source.Task.TaskId, source.Task.Revision);

    internal TaskTestSnapshot Persist(TaskTestDocument document)
    {
        Require(document);
        return new(document, new ImmutableBlobStore(instance, contracts).PersistPlan(NotificationCatalogCodec.Encode(document)));
    }

    internal TaskTestSnapshot Read(BlobReference reference, SqliteConnection connection, SqliteTransaction transaction)
    {
        var document = new NotificationCatalogCodec(instance, contracts).ReadDocument<TaskTestDocument>(reference);
        Require(document);
        var result = new TaskTestSnapshot(document, reference);
        TaskTestRows.RequireSaved(connection, transaction, result);
        var catalog = new NotificationCatalogCodec(instance, contracts).Read(document.Catalog);
        if (catalog.Catalog.Revision != document.ExpectedCatalogRevision || !catalog.Document.Tasks.Contains(document.Task))
            throw Invalid();
        new NotificationCatalogCodec(instance, contracts).RequirePublishedCursor(connection, transaction, catalog);
        foreach (var field in document.Secrets)
        {
            NotificationDeliveryRows.RequirePayload(connection, transaction, field, allowRetired: true);
            NotificationCatalogRows.RequireFieldProvenance(connection, transaction, instance.InstanceId,
                document.Task.TaskId, NexusPipeline.Core.Secrets.NotificationSecretFields.Name(field.Field),
                field.Reference, document.ExpectedCatalogRevision);
        }
        return result;
    }

    internal NotificationDeliveryDocument Delivery(TaskTestSnapshot source)
    {
        var document = source.Document;
        return new("nexus.notification.delivery", 1, instance.InstanceId, DeliveryId(document),
            document.OperationId, document.SubjectId, new("savedTaskTest", null, source.Reference),
            document.Catalog, document.ExpectedCatalogRevision, document.Task, Title, Body, document.Secrets, null);
    }

    internal static Guid DeliveryId(TaskTestDocument source) =>
        ReceiptRouteCodec.DeliveryId(source.OperationId, 0, source.Task.TaskId);

    private void Require(TaskTestDocument document)
    {
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot ||
            document.Format != Format || document.Version != 1 || document.InstanceId != instance.InstanceId ||
            document.Task is null) throw Invalid();
        _ = Identity(document);
        NotificationCatalogCodec.RequirePlan(document.Catalog);
        NotificationCatalogCodec.RequirePlan(document.Task.Definition);
        NotificationDeliveryCodec.RequireSecrets(document.Secrets);
    }

    internal static IOException Invalid() => new("notification_saved_test_unbound");
}
