using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal static class NotificationWorkShape
{
    internal static void Require(NotificationWorkDocument document, string format, Guid instanceId)
    {
        if (document.Format != format || document.Version != 1 || document.InstanceId != instanceId || document.OperationId == Guid.Empty ||
            document.AcceptedConsumedMilliseconds < 0 || document.CatalogRevision < 0 || document.CatalogRevision > 9007199254740991 ||
            string.IsNullOrWhiteSpace(document.Title) || document.Title.Contains('\0') || document.Title.Length > 200 ||
            string.IsNullOrWhiteSpace(document.Body) || document.Body.Contains('\0') || document.Body.Length > 4000 ||
            document.Targets.IsDefault || document.Targets.Length is < 1 or > 99 ||
            document.Targets.Any(item => item is null || item.TaskId == Guid.Empty || item.Secrets.IsDefault ||
                item.Disposition is not ("missing" or "disabled" or "queued") || (item.Disposition == "missing") != (item.Task is null)) ||
            document.Targets.Select(item => item.TaskId).Distinct().Count() != document.Targets.Length) throw Invalid();
        NotificationCatalogCodec.RequirePlan(document.Invocation); NotificationCatalogCodec.RequirePlan(document.Begin);
        NotificationCatalogCodec.RequirePlan(document.Catalog);
        foreach (var target in document.Targets) if (target.Task is { } task) NotificationCatalogCodec.RequirePlan(task.Definition);
    }

    internal static void RequireCatalog(RuntimeInstance instance, ContractReader contracts, NotificationWorkDocument document,
        SqliteConnection connection, SqliteTransaction transaction)
    {
        var catalog = new NotificationCatalogCodec(instance, contracts).Read(document.Catalog);
        if (catalog.Catalog.Revision != document.CatalogRevision) throw Invalid();
        new NotificationCatalogCodec(instance, contracts).RequirePublishedCursor(connection, transaction, catalog);
        foreach (var target in document.Targets)
        {
            var task = catalog.Catalog.Tasks.SingleOrDefault(item => item.Id == target.TaskId);
            var expected = task is null ? "missing" : task.IsEnabled ? "queued" : "disabled";
            if (target.Disposition != expected || target.Task != catalog.Document.Tasks.SingleOrDefault(item => item.TaskId == target.TaskId)) throw Invalid();
            if (expected != "queued") { if (!target.Secrets.IsEmpty) throw Invalid(); continue; }
            NotificationDeliveryCodec.RequireSecrets(target.Secrets);
            foreach (var field in target.Secrets)
            {
                NotificationDeliveryRows.RequirePayload(connection, transaction, field, allowRetired: true);
                NotificationCatalogRows.RequireFieldProvenance(connection, transaction, document.InstanceId, target.TaskId,
                    NotificationSecretFields.Name(field.Field), field.Reference, document.CatalogRevision);
            }
        }
    }

    internal static NotificationDeliveryDocument Delivery(Guid instanceId, NotificationWorkSnapshot source, NotificationWorkTarget target,
        string subject, string kind)
    {
        if (target.Disposition != "queued" || target.Task is null) throw Invalid();
        return new("nexus.notification.delivery", 1, instanceId, NotificationScheduledCodec.DeliveryId(source.Document.OperationId, target.TaskId),
            source.Document.OperationId, subject, new(kind, null, source.Reference), source.Document.Catalog, source.Document.CatalogRevision,
            target.Task, source.Document.Title, source.Document.Body, target.Secrets, null);
    }

    internal static IOException Invalid() => new("notification_work_source_unbound");
}
