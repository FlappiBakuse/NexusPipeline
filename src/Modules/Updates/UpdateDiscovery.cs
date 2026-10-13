using System.Net;
using System.Text.Json.Nodes;
using NexusPipeline.Shared.Versioning;

namespace NexusPipeline.Modules.Updates;

internal sealed record UpdateDiscoveryResult(ReleaseInfo? Release, string? PendingVersion = null,
    string? PendingStatus = null, string? PendingError = null);

internal static class UpdateDiscovery
{
    private sealed record Target(string Tag, string ManifestName, int Reader, string[] Capabilities);
    private sealed record Bridge(int SourceReader, string[] SourceCapabilities, int TargetReader,
        string[] TargetCapabilities, Target Entry);
    private const int MetadataLimit = 2 * 1024 * 1024;

    internal static async Task<UpdateDiscoveryResult> FetchAsync(HttpClient http, UpdateSourcePolicy policy,
        string channel, NexusVersion current, CancellationToken token)
    {
        Uri entry = policy.IsDefaultSource ? new(UpdateRemoteResourceRules.DefaultDiscoveryUrl) : policy.SourceUri;
        using var response = await policy.GetAsync(http, entry,
            policy.IsDefaultSource ? UpdateResourceKind.Discovery : UpdateResourceKind.Manifest, "NexusPipeline-update", token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new IOException($"update.discovery_http_{(int)response.StatusCode}");
        JsonNode root = UpdateJson.Parse(await UpdateJson.ReadBoundedAsync(response.Content, MetadataLimit, token).ConfigureAwait(false));
        if (!policy.IsDefaultSource && root is JsonArray)
            return new(UpdateCatalog.PickRelease(root, channel, current));
        Object(root, "schemaVersion", "channels", "bridges");
        Require(Integer(root, "schemaVersion") == 1, "protocol");
        Object(root["channels"], "stable", "prerelease");
        Bridge[] bridges = ReadBridges(root["bridges"]);
        foreach (string name in new[] { "stable", "prerelease" })
            Object(root["channels"]![name], "previousPublishedTarget", "pendingTarget");
        var selected = root["channels"]![channel]!;
        Target? previous = ReadTarget(selected["previousPublishedTarget"]);
        Target? pending = ReadTarget(selected["pendingTarget"]);
        var previousResult = await ResolveAsync(previous).ConfigureAwait(false);
        var pendingResult = await ResolveAsync(pending).ConfigureAwait(false);
        ReleaseInfo? best = previousResult.Release;
        if (pendingResult.Release is not null && (best is null || pendingResult.Release.Version.CompareTo(best.Version) > 0))
            best = pendingResult.Release;
        return new(best, pending?.Tag, pendingResult.Status, pendingResult.Error);

        async Task<(ReleaseInfo? Release, string? Status, string? Error)> ResolveAsync(Target? target, bool allowBridge = true)
        {
            if (target is null) return (null, null, null);
            UpdateCatalog.TryParseTag(target.Tag, out var version);
            if (version.CompareTo(current) <= 0) return (null, "current-or-older", null);
            if (!Supported(target))
            {
                if (!allowBridge) return (null, "unsupported-capability", "update.discovery_capability");
                var eligible = bridges.Where(bridge => bridge.SourceReader == 1
                    && bridge.SourceCapabilities.Order(StringComparer.Ordinal).SequenceEqual(UpdateTargetManifest.Capabilities.Order(StringComparer.Ordinal))
                    && bridge.TargetReader == target.Reader && target.Capabilities.All(bridge.TargetCapabilities.Contains)
                    && Supported(bridge.Entry))
                    .Select(bridge => (bridge.Entry, Version: VersionOf(bridge.Entry)))
                    .Where(bridge => bridge.Version.CompareTo(current) > 0 && bridge.Version.CompareTo(version) < 0)
                    .OrderByDescending(bridge => bridge.Version).ToArray();
                if (eligible.Length == 0) return (null, "unsupported-capability", "update.discovery_capability");
                foreach (var bridge in eligible)
                {
                    var resolved = await ResolveAsync(bridge.Entry, allowBridge: false).ConfigureAwait(false);
                    if (resolved.Release is not null) return (resolved.Release, "bridge-required", null);
                }
                return (null, "bridge-unavailable", "update.discovery_bridge_unavailable");
            }
            try
            {
                Uri releaseUri = new(policy.SourceUri.AbsoluteUri.TrimEnd('/') + "/tags/" + Uri.EscapeDataString(target.Tag));
                using var releaseResponse = await policy.GetAsync(http, releaseUri, UpdateResourceKind.ReleaseMetadata,
                    "NexusPipeline-update", token).ConfigureAwait(false);
                if (releaseResponse.StatusCode == HttpStatusCode.NotFound) return (null, "not-published", null);
                if (!releaseResponse.IsSuccessStatusCode) return (null, "release-unavailable", $"update.release_http_{(int)releaseResponse.StatusCode}");
                JsonNode release = UpdateJson.Parse(await UpdateJson.ReadBoundedAsync(releaseResponse.Content, MetadataLimit, token).ConfigureAwait(false));
                Require(release is JsonObject && Text(release, "tag_name") == target.Tag, "release_identity");
                if (Boolean(release, "draft")) return (null, "not-published", null);
                long id = Integer(release, "id");
                Require(id > 0, "release_identity");
                var assets = new JsonArray();
                var ids = new HashSet<long>();
                var names = new HashSet<string>(StringComparer.Ordinal);
                for (int page = 1; ; page++)
                {
                    Require(page <= 5, "asset_pages");
                    Uri assetsUri = new(policy.SourceUri.AbsoluteUri.TrimEnd('/') + $"/{id}/assets?per_page=100&page={page}");
                    using var assetResponse = await policy.GetAsync(http, assetsUri, UpdateResourceKind.ReleaseMetadata,
                        "NexusPipeline-update", token).ConfigureAwait(false);
                    Require(assetResponse.IsSuccessStatusCode, "assets_unavailable");
                    JsonNode pageRoot = UpdateJson.Parse(await UpdateJson.ReadBoundedAsync(assetResponse.Content, MetadataLimit, token).ConfigureAwait(false));
                    Require(pageRoot is JsonArray { Count: <= 100 }, "asset_page");
                    foreach (JsonNode? asset in (JsonArray)pageRoot)
                    {
                        Require(asset is JsonObject && ids.Add(Integer(asset, "id")) && names.Add(Text(asset, "name")), "asset_duplicate");
                        assets.Add(asset!.DeepClone());
                    }
                    Require(assets.Count <= 400, "asset_count");
                    if (((JsonArray)pageRoot).Count < 100) break;
                }
                release["assets"] = assets;
                ReleaseInfo? candidate = UpdateCatalog.PickRelease(new JsonArray(release), channel, current);
                Require(candidate?.TargetManifestAsset?.Name == target.ManifestName, "release_assets");
                byte[] manifestBytes = await UpdateAssetDownloader.ReadAsync(http, policy, candidate!.TargetManifestAsset!,
                    UpdateTargetManifest.MaxBytes, token).ConfigureAwait(false);
                UpdateTargetManifest manifest = UpdateTargetManifest.Parse(manifestBytes, candidate);
                return (candidate with { FrozenTarget = manifest }, "ready", null);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException or InvalidOperationException
                or HttpRequestException or IOException or OperationCanceledException)
            {
                return (null, "invalid-target", error is InvalidDataException ? error.Message : "update.discovery_target_unavailable");
            }
        }
    }

    private static bool Supported(Target target) => target.Reader == 1
        && target.Capabilities.All(UpdateTargetManifest.Capabilities.Contains);
    private static NexusVersion VersionOf(Target target)
    {
        UpdateCatalog.TryParseTag(target.Tag, out var version);
        return version;
    }
    private static Bridge[] ReadBridges(JsonNode? value)
    {
        Require(value is JsonArray { Count: <= 8 }, "bridges");
        var entries = new HashSet<string>(StringComparer.Ordinal);
        return ((JsonArray)value!).Select(item =>
        {
            Object(item, "sourceReaderProtocol", "sourceCapabilities", "targetReaderProtocol", "targetCapabilities", "entryTarget");
            long source = Integer(item, "sourceReaderProtocol"), target = Integer(item, "targetReaderProtocol");
            Require(source is >= 1 and <= int.MaxValue && target is >= 1 and <= int.MaxValue, "bridge_protocol");
            Target? entry = ReadTarget(item!["entryTarget"]);
            Require(entry is not null && entries.Add(entry.Tag), "bridge_identity");
            return new Bridge((int)source, ReadCapabilities(item["sourceCapabilities"]),
                (int)target, ReadCapabilities(item["targetCapabilities"]), entry!);
        }).ToArray();
    }
    private static string[] ReadCapabilities(JsonNode? value)
    {
        Require(value is JsonArray { Count: <= 16 }, "capabilities");
        string[] capabilities = ((JsonArray)value!).Select(String).ToArray();
        Require(capabilities.Distinct(StringComparer.Ordinal).Count() == capabilities.Length, "capabilities");
        return capabilities;
    }

    private static Target? ReadTarget(JsonNode? value)
    {
        if (value is null) return null;
        Object(value, "releaseTag", "manifestAssetName", "readerProtocol", "requiredCapabilities");
        string tag = Text(value, "releaseTag"), manifest = Text(value, "manifestAssetName");
        Require(UpdateCatalog.TryParseTag(tag, out var version) && tag == "v" + version
            && manifest == $"NexusPipeline-v{version}-win-x64-update.json", "target_identity");
        long protocol = Integer(value, "readerProtocol");
        Require(protocol is >= 1 and <= int.MaxValue, "protocol");
        string[] capabilities = ReadCapabilities(value["requiredCapabilities"]);
        return new(tag, manifest, (int)protocol, capabilities);
    }

    private static void Object(JsonNode? node, params string[] fields)
    {
        Require(node is JsonObject obj && obj.Count == fields.Length && fields.All(obj.ContainsKey), "fields");
    }
    private static string Text(JsonNode? node, string name) => String(node?[name]);
    private static string String(JsonNode? node)
    {
        Require(node is JsonValue value && value.TryGetValue<string>(out string? text) && text.Length is > 0 and <= 256, "string");
        return node!.GetValue<string>();
    }
    private static long Integer(JsonNode? node, string name)
    {
        Require(node?[name] is JsonValue value && value.TryGetValue<long>(out _), "integer");
        return node![name]!.GetValue<long>();
    }
    private static bool Boolean(JsonNode node, string name)
    {
        Require(node[name] is JsonValue value && value.TryGetValue<bool>(out _), "boolean");
        return node[name]!.GetValue<bool>();
    }
    private static void Require(bool condition, string code)
    {
        if (!condition) throw new InvalidDataException("update.discovery_" + code);
    }
}
