using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal sealed class UpdateFeedCodec(ContractReader contracts)
{
    internal HostUpdateFeed Read(ReadOnlyMemory<byte> original, HostUpdateChannel channel)
    {
        if (original.Length is < 1 or > StrictJson.MaximumBytes || original.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) throw Invalid();
        var bytes = original.ToArray();
        using var document = contracts.Read(bytes, "update-feed.schema.json");
        var root = document.RootElement;
        if (root.GetProperty("channel").GetString() != Wire(channel)) throw Invalid();
        var identity = new UpdateFeedIdentity(channel, root.GetProperty("sequence").GetInt64(),
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
        UpdateFeedPolicy.Require(identity, 1, null);
        var versions = new HashSet<string>(StringComparer.Ordinal);
        var artifacts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var releaseIds = new HashSet<long>(); var assetIds = new HashSet<long>();
        var releases = ImmutableArray.CreateBuilder<HostUpdateRelease>();
        foreach (var item in root.GetProperty("releases").EnumerateArray())
        {
            var version = item.GetProperty("hostVersion").GetString()!;
            var minimum = item.GetProperty("minReaderVersion").GetString()!;
            var artifact = item.GetProperty("artifact"); var url = artifact.GetProperty("url").GetString()!;
            var releaseId = item.GetProperty("releaseId").GetInt64(); var assetId = item.GetProperty("assetId").GetInt64();
            if (!versions.Add(version) || !artifacts.Add(url) || !Version.TryParse(minimum.Split('-')[0], out _) ||
                !releaseIds.Add(releaseId) || !assetIds.Add(assetId) ||
                channel == HostUpdateChannel.Stable && version.Contains('-')) throw Invalid();
            var store = item.GetProperty("storeBinding");
            var binding = new UpdateStoreBinding(store.GetProperty("channel").GetString() switch
                { "stable" => ExtensionStoreChannel.Stable, "develop" => ExtensionStoreChannel.Develop, _ => throw Invalid() },
                store.GetProperty("generationId").GetGuid(), store.GetProperty("sequence").GetInt64(),
                store.GetProperty("catalogUrl").GetString()!, store.GetProperty("catalogSha256").GetString()!,
                store.GetProperty("signatureUrl").GetString()!);
            var published = DateTimeOffset.Parse(item.GetProperty("publishedAt").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.None);
            if (published.Offset != TimeSpan.Zero) throw Invalid();
            releases.Add(new(version, minimum, new(url, artifact.GetProperty("sizeBytes").GetInt64(),
                artifact.GetProperty("sha256").GetString()!), item.GetProperty("payloadManifestSha256").GetString()!, published, binding, releaseId, assetId));
        }
        return new(identity, releases.ToImmutable());
    }

    private static string Wire(HostUpdateChannel value) => value switch
    { HostUpdateChannel.Stable => "stable", HostUpdateChannel.Prerelease => "prerelease", _ => throw Invalid() };
    private static IOException Invalid() => new("update_feed_shape_invalid");
}
