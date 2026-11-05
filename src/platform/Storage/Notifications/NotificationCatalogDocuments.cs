using System.Collections.Immutable;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;

namespace NexusPipeline.Platform.Storage.Notifications;

internal sealed record NotificationTaskDocument(
    [property: JsonRequired] string Format, [property: JsonRequired] int Version,
    [property: JsonRequired] Guid InstanceId, [property: JsonRequired] NotificationTaskDefinition Task);
internal sealed record NotificationCatalogEntry(
    [property: JsonRequired] Guid TaskId, [property: JsonRequired] long Revision,
    [property: JsonRequired] BlobReference Definition);
internal sealed record NotificationCatalogDocument(
    [property: JsonRequired] string Format, [property: JsonRequired] int Version,
    [property: JsonRequired] Guid InstanceId, [property: JsonRequired] long Revision,
    [property: JsonRequired] int MaximumTasks,
    [property: JsonRequired] ImmutableArray<NotificationCatalogEntry> Tasks);
internal sealed record NotificationCatalogSnapshot(NotificationTaskCatalog Catalog,
    NotificationCatalogDocument Document, BlobReference Reference);

internal enum NotificationCatalogChange { Save, Delete, Reorder, Limit }
internal sealed record NotificationCatalogRequest(
    [property: JsonRequired] NotificationCatalogChange Change,
    [property: JsonRequired] long ExpectedCatalogRevision,
    [property: JsonRequired] Guid? TaskId, [property: JsonRequired] long? ExpectedTaskRevision,
    [property: JsonRequired] NotificationTaskInput? Input,
    [property: JsonRequired] ImmutableArray<Guid> Order, [property: JsonRequired] int? MaximumTasks);
internal sealed record NotificationSecretChange(
    [property: JsonRequired] NotificationSecretField Field,
    [property: JsonRequired] Guid OperationId,
    [property: JsonRequired] SecretDisposition Disposition,
    [property: JsonRequired] SecretFieldReference Before, [property: JsonRequired] SecretFieldReference After,
    [property: JsonRequired] bool ResultReadable, [property: JsonRequired] string? ReplayProof,
    [property: JsonRequired] Guid? JournalId, [property: JsonRequired] BlobReference? Preparation,
    [property: JsonRequired] NotificationSecretRetirement? Retirement = null);
internal sealed record NotificationPublicationDocument(
    [property: JsonRequired] string Format, [property: JsonRequired] int Version,
    [property: JsonRequired] Guid InstanceId, [property: JsonRequired] Guid OperationId,
    [property: JsonRequired] string SubjectId, [property: JsonRequired] string RequestDigest,
    [property: JsonRequired] NotificationCatalogRequest Request,
    [property: JsonRequired] BlobReference Before, [property: JsonRequired] BlobReference After,
    [property: JsonRequired] ImmutableArray<NotificationSecretChange> Secrets);

internal sealed record NotificationPublicationSnapshot(NotificationPublicationDocument Document,
    BlobReference Reference, NotificationCatalogSnapshot Before, NotificationCatalogSnapshot After);

internal sealed record NotificationCatalogCursor(long Revision, BlobReference Reference);

internal sealed record NotificationSecretRetirement(
    [property: JsonRequired] string State, [property: JsonRequired] Guid? JournalId,
    [property: JsonRequired] BlobReference? Plan);
