using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Scheduling;

internal sealed record ScheduleMinuteDocument(string Format, int Version, Guid InstanceId, string SubjectId,
    DateTimeOffset ObservedAtUtc, long SettingsRevision, ScheduleMinuteScan Scan);

internal static class ScheduleMinuteCodec
{
    internal const string Kind = "schedule.minute";
    internal const string Subject = "host:scheduler";
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal static Guid OperationId(Guid instanceId, long utcMinute) => new(
        SHA256.HashData(Encoding.UTF8.GetBytes("nexus.schedule.minute/1/" + instanceId.ToString("D") + "/" +
            utcMinute.ToString(System.Globalization.CultureInfo.InvariantCulture))).AsSpan(0, 16), bigEndian: true);
    internal static byte[] Encode(ScheduleMinuteDocument document) => BoundedJson.Serialize(document, Options);
    internal static OperationIdentity Identity(ScheduleMinuteDocument document) => new(
        OperationId(document.InstanceId, document.ObservedAtUtc.ToUnixTimeSeconds() / 60), document.SubjectId,
        Convert.ToHexStringLower(SHA256.HashData(Encode(document))), Kind);

    internal static ScheduleMinuteDocument Decode(byte[] bytes, Guid instanceId)
    {
        using var json = StrictJson.Parse(bytes);
        var value = json.RootElement.Deserialize<ScheduleMinuteDocument>(Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(json.RootElement, JsonSerializer.SerializeToElement(value, Options)) ||
            value.Format != "nexus.schedule.minute" || value.Version != 1 || value.InstanceId != instanceId ||
            value.SubjectId != Subject || value.ObservedAtUtc.Offset != TimeSpan.Zero ||
            value.ObservedAtUtc < DateTimeOffset.UnixEpoch || value.SettingsRevision < 0 || value.Scan is null ||
            value.Scan.ExpectedWatermarkRevision < 0 || value.Scan.CatalogRevision < 0 ||
            value.Scan.HighestUtcMinute != value.ObservedAtUtc.ToUnixTimeSeconds() / 60 ||
            value.Scan.Occurrences.IsDefault || value.Scan.Occurrences.Length > 4096 ||
            value.Scan.Occurrences.Select(item => item.OccurrenceId).Distinct().Count() != value.Scan.Occurrences.Length)
            throw Invalid();
        foreach (var item in value.Scan.Occurrences)
            if (item.InstanceInvalid() || item.CatalogRevision != value.Scan.CatalogRevision ||
                item.TriggerUtcMinute != value.Scan.HighestUtcMinute) throw Invalid();
        return value;
    }

    private static bool InstanceInvalid(this ScheduledOccurrenceProposal item) => item is null ||
        new[] { item.OccurrenceId, item.RunId, item.StartOperationId, item.ScheduleId, item.QueueId }.Any(id => id == Guid.Empty) ||
        item.ScheduleRevision is < 1 or > 9007199254740991 || item.QueueRevision is < 1 or > 9007199254740991 ||
        item.LocalMinute is < 0 or > 1439 || string.IsNullOrWhiteSpace(item.TimeZoneId) || item.TimeZoneId.Length > 128;
    internal static IOException Invalid() => new("schedule_minute_metadata_unbound");
}
