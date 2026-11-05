using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace NexusPipeline.Platform.Storage.Notifications;

internal sealed record NotificationWorkTarget(
    [property: JsonRequired] Guid TaskId,
    [property: JsonRequired] string Disposition,
    [property: JsonRequired] NotificationCatalogEntry? Task,
    [property: JsonRequired] ImmutableArray<NotificationDeliverySecret> Secrets);
internal sealed record NotificationWorkDocument(
    [property: JsonRequired] string Format, [property: JsonRequired] int Version,
    [property: JsonRequired] Guid InstanceId, [property: JsonRequired] Guid OperationId,
    [property: JsonRequired] BlobReference Invocation, [property: JsonRequired] BlobReference Begin,
    [property: JsonRequired] long AcceptedConsumedMilliseconds,
    [property: JsonRequired] BlobReference Catalog, [property: JsonRequired] long CatalogRevision,
    [property: JsonRequired] string Title, [property: JsonRequired] string Body,
    [property: JsonRequired] ImmutableArray<NotificationWorkTarget> Targets);
internal sealed record NotificationWorkSnapshot(NotificationWorkDocument Document, BlobReference Reference);
