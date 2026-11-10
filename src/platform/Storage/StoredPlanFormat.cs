using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage;

internal static class StoredPlanFormat
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    internal static bool IsReference(BlobReference? reference) =>
        reference is not null && reference.Sha256 is { Length: 64 } &&
        reference.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
        reference.SizeBytes is >= 1 and <= StrictJson.MaximumBytes && reference.RelativePath == $"blobs/plans/{reference.Sha256}.json";
}
