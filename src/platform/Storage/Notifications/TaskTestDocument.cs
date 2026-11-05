using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace NexusPipeline.Platform.Storage.Notifications;

internal sealed record TaskTestDocument(
    [property: JsonRequired] string Format, [property: JsonRequired] int Version,
    [property: JsonRequired] Guid InstanceId, [property: JsonRequired] Guid OperationId,
    [property: JsonRequired] string SubjectId, [property: JsonRequired] long ExpectedCatalogRevision,
    [property: JsonRequired] BlobReference Catalog, [property: JsonRequired] NotificationCatalogEntry Task,
    [property: JsonRequired] ImmutableArray<NotificationDeliverySecret> Secrets);
internal sealed record TaskTestSnapshot(TaskTestDocument Document, BlobReference Reference);
