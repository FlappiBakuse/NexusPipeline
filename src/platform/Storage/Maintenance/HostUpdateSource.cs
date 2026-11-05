using NexusPipeline.Core.Maintenance;
using System.Text.Json.Serialization;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal sealed record HostUpdateSource(string Format, int Version, Guid InstanceId, Guid OperationId, Guid JournalId,
    SavedUpdateFeed Feed, string HostVersion, BlobReference PayloadManifest, BlobReference Archive,
    HostUpdateTiming Timing, UpdateWindowIntent Window)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BlobReference? Installation { get; init; }
}
