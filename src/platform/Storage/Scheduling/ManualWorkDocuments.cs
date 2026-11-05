using System.Text.Json.Serialization;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Scheduling;

internal sealed record ManualWorkDocument(
    [property: JsonRequired] string Format, [property: JsonRequired] int Version,
    [property: JsonRequired] Guid InstanceId, [property: JsonRequired] Guid OperationId,
    [property: JsonRequired] string CallerSubject, [property: JsonRequired] Guid ClientId,
    [property: JsonRequired] string ExtensionId, [property: JsonRequired] string ProviderId,
    [property: JsonRequired] Guid ActivationId, [property: JsonRequired] Guid OwnerGeneration,
    [property: JsonRequired] DateTimeOffset StartedAtUtc, [property: JsonRequired] DateTimeOffset ObservedAtUtc,
    [property: JsonRequired] DateOnly LocalDate, [property: JsonRequired] string LocalTimeZoneId,
    [property: JsonRequired] ManualWorkTask Task,
    [property: JsonRequired] FrozenScheduledInvocation Invocation, [property: JsonRequired] JournalWriter Creator,
    [property: JsonRequired] long InitialConsumedMilliseconds);
internal sealed record ManualWorkSnapshot(ManualWorkDocument Document, BlobReference Reference);
internal sealed record ManualWorkBegin(
    [property: JsonRequired] string Format, [property: JsonRequired] int Version,
    [property: JsonRequired] BlobReference Source, [property: JsonRequired] DateTimeOffset ObservedAtUtc,
    [property: JsonRequired] long ConsumedMilliseconds);
internal sealed record ManualWorkResult(
    [property: JsonRequired] string Format, [property: JsonRequired] int Version,
    [property: JsonRequired] BlobReference Source, [property: JsonRequired] BlobReference? Begin,
    [property: JsonRequired] string Completion, [property: JsonRequired] ScheduledBusinessResult? Returned,
    [property: JsonRequired] long? FinalConsumedMilliseconds, [property: JsonRequired] DateTimeOffset ObservedAtUtc,
    [property: JsonRequired] bool WallClockRegressed, [property: JsonRequired] string ReasonCode);
internal sealed record ManualWorkState(ManualWorkSnapshot Source, BlobReference? BeginReference, ManualWorkBegin? Begin,
    BlobReference? ResultReference, ManualWorkResult? Result);
