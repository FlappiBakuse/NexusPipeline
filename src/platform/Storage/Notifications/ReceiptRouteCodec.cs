using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal sealed class ReceiptRouteCodec(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal static Guid DeliveryId(Guid source, int ordinal, Guid task) => new(SHA256.HashData(
        Encoding.UTF8.GetBytes($"notification-delivery/{source:D}/{ordinal}/{task:D}")).AsSpan(0, 16));

    internal (ReceiptRouteDocument Document, BlobReference Reference,
        ImmutableArray<NotificationDeliverySnapshot> Deliveries) Prepare(ReceiptReadContext source, int ordinal)
    {
        if (ordinal < 0 || ordinal >= source.Routes.Length) throw NotificationReceiptCodec.Invalid();
        var route = source.Routes[ordinal];
        var targets = ImmutableArray.CreateBuilder<ReceiptRouteTarget>();
        var deliveries = ImmutableArray.CreateBuilder<NotificationDeliverySnapshot>();
        foreach (var taskId in route.Selection.TaskIds)
        {
            var task = source.Catalog.Catalog.Tasks.SingleOrDefault(task => task.Id == taskId);
            if (task is null || !task.IsEnabled)
            {
                targets.Add(new(taskId, task is null ? "missing" : "disabled", null, null));
                continue;
            }
            var document = Delivery(source, ordinal, taskId);
            var saved = new NotificationDeliveryCodec(instance, contracts).Persist(document);
            targets.Add(new(taskId, "queued", document.OperationId, saved.Reference));
            deliveries.Add(saved);
        }
        var result = new ReceiptRouteDocument("nexus.notification.receipt-route", 1,
            source.Snapshot.Document.OperationId, ordinal, Kind(route), route.SourceId, targets.ToImmutable());
        Require(result, source);
        var reference = new ImmutableBlobStore(instance, contracts).PersistPlan(NotificationCatalogCodec.Encode(result));
        return (result, reference, deliveries.ToImmutable());
    }

    internal ReceiptRouteDocument Read(BlobReference reference, ReceiptReadContext source,
        SqliteConnection connection, SqliteTransaction transaction)
    {
        var document = new NotificationCatalogCodec(instance, contracts).ReadDocument<ReceiptRouteDocument>(reference);
        Require(document, source);
        ReceiptRouteRows.RequireSaved(connection, transaction, document, reference);
        foreach (var target in document.Targets.Where(item => item.Disposition == "queued"))
        {
            var delivery = new NotificationDeliveryCodec(instance, contracts).Read(target.Delivery!);
            RequireDelivery(delivery, source, document.Ordinal);
            NotificationDeliveryRows.RequireSaved(connection, transaction, delivery);
            ReceiptRouteRows.RequireMembership(connection, transaction, delivery);
        }
        return document;
    }

    internal void RequireOrigin(NotificationDeliverySnapshot delivery, SqliteConnection connection, SqliteTransaction transaction)
    {
        var document = delivery.Document;
        if (document.Registration is null || document.RouteOrdinal is null) throw NotificationReceiptCodec.Invalid();
        var source = new NotificationReceiptCodec(instance, database, contracts).ReadContext(document.Registration, connection, transaction);
        RequireDelivery(delivery, source, document.RouteOrdinal.Value);
        ReceiptRouteRows.RequireMembership(connection, transaction, delivery);
        var reference = ReceiptRouteRows.Find(connection, transaction, source.Snapshot.Document.OperationId,
            document.RouteOrdinal.Value) ?? throw NotificationReceiptCodec.Invalid();
        var route = Read(reference, source, connection, transaction);
        if (!route.Targets.Any(target => target.Delivery == delivery.Reference && target.DeliveryOperationId == document.OperationId))
            throw NotificationReceiptCodec.Invalid();
    }

    private NotificationDeliveryDocument Delivery(ReceiptReadContext source, int ordinal, Guid taskId)
    {
        var original = source.Snapshot.Document;
        var route = source.Routes[ordinal];
        var task = source.Catalog.Catalog.Tasks.Single(task => task.Id == taskId && task.IsEnabled);
        var text = NotificationReceiptText.Render(source, ordinal);
        var fields = original.Tasks.Single(item => item.TaskId == taskId).Secrets;
        var entry = source.Catalog.Document.Tasks.Single(item => item.TaskId == taskId);
        return new("nexus.notification.delivery", 1, original.InstanceId, DeliveryId(original.OperationId, ordinal, taskId),
            original.OperationId, original.SubjectId, new(original.Receipt, original.TotalPlan, original.DefinitionBundle,
            Kind(route), route.SourceId), original.Catalog, original.CatalogRevision, entry, text.Title, text.Body,
            fields, task.Configuration.ScreenshotEnabled ? original.Screenshot : null, source.Snapshot.Reference, ordinal);
    }

    private void RequireDelivery(NotificationDeliverySnapshot saved, ReceiptReadContext source, int ordinal)
    {
        if (ordinal < 0 || ordinal >= source.Routes.Length ||
            !source.Routes[ordinal].Selection.TaskIds.Contains(saved.Document.Task.TaskId)) throw NotificationReceiptCodec.Invalid();
        var expected = Delivery(source, ordinal, saved.Document.Task.TaskId);
        if (!NotificationCatalogCodec.Encode(saved.Document).AsSpan().SequenceEqual(NotificationCatalogCodec.Encode(expected)))
            throw NotificationReceiptCodec.Invalid();
    }

    private static void Require(ReceiptRouteDocument document, ReceiptReadContext source)
    {
        if (document.Format != "nexus.notification.receipt-route" || document.Version != 1 ||
            document.SourceOperationId != source.Snapshot.Document.OperationId || document.Ordinal < 0 ||
            document.Ordinal >= source.Routes.Length || document.Targets.IsDefault ||
            document.Targets.Any(item => item is null)) throw NotificationReceiptCodec.Invalid();
        var route = source.Routes[document.Ordinal];
        if (document.Kind != Kind(route) || document.SourceId != route.SourceId ||
            !document.Targets.Select(item => item.TaskId).SequenceEqual(route.Selection.TaskIds)) throw NotificationReceiptCodec.Invalid();
        foreach (var target in document.Targets)
        {
            var task = source.Catalog.Catalog.Tasks.SingleOrDefault(item => item.Id == target.TaskId);
            var disposition = task is null ? "missing" : !task.IsEnabled ? "disabled" : "queued";
            if (target.Disposition != disposition || disposition == "queued" &&
                (target.DeliveryOperationId != DeliveryId(document.SourceOperationId, document.Ordinal, target.TaskId) || target.Delivery is null) ||
                disposition != "queued" && (target.DeliveryOperationId is not null || target.Delivery is not null))
                throw NotificationReceiptCodec.Invalid();
            if (target.Delivery is { } reference) NotificationCatalogCodec.RequirePlan(reference);
        }
    }

    private static string Kind(NotificationRoute route) => route.Kind switch
    {
        NotificationRoutingKind.Binding => "binding", NotificationRoutingKind.Queue => "queue",
        _ => throw NotificationReceiptCodec.Invalid()
    };
}
