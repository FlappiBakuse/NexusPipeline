using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Shared.Versioning;
using Xunit;

namespace NexusPipeline.Tests.Updates;

public sealed class UpdateDiscoveryTests
{
    private readonly Dictionary<string, JsonObject> _releases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _manifests = new(StringComparer.Ordinal);
    private readonly List<string> _requests = [];
    private readonly JsonObject _entry;
    private Func<Uri, HttpResponseMessage?>? _intercept;

    public UpdateDiscoveryTests()
    {
        AddRelease("0.17.4", 4);
        AddRelease("0.17.5", 5);
        _entry = new JsonObject { ["schemaVersion"] = 1, ["bridges"] = new JsonArray(),
            ["channels"] = new JsonObject {
                ["stable"] = new JsonObject { ["previousPublishedTarget"] = null, ["pendingTarget"] = null },
                ["prerelease"] = new JsonObject { ["previousPublishedTarget"] = Target("0.17.4"), ["pendingTarget"] = Target("0.17.5") } } };
    }

    [Fact]
    public async Task ManualAdoptionRequiresPublishedExactOfficialAssetsWithoutConsultingDiscovery()
    {
        string prefix = "NexusPipeline-v0.17.4-win-x64-";
        var release = _releases["0.17.4"];
        release["published_at"] = "2026-10-14T00:00:00Z";
        var assets = new JsonArray(Asset(prefix + "Setup.exe", 401, [1]),
            Asset(prefix + "Portable.zip", 402, [2]), Asset(prefix + "Update.zip", 403, [3]));
        foreach (var asset in assets)
            asset!["browser_download_url"] = "https://github.com/FlappiBakuse/NexusPipeline/releases/download/v0.17.4/" + asset["name"];
        release["assets"] = assets;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            string route = request.RequestUri!.AbsolutePath;
            Assert.Equal("api.github.com", request.RequestUri.Host);
            _requests.Add(route);
            return route.EndsWith("/tags/v0.17.4", StringComparison.Ordinal) ? Json(release)
                : route.EndsWith("/4/assets", StringComparison.Ordinal) ? Json(assets)
                : throw new InvalidOperationException("Unexpected proof request: " + route);
        }));
        var target = await UpdateDiscovery.FetchOfficialTargetAsync(http, "0.17.4", NexusVersion.Stable(0, 17, 3), CancellationToken.None);
        Assert.Equal("v0.17.4", target.Tag);
        Assert.Equal(2, _requests.Count);
        release["published_at"] = null;
        await Assert.ThrowsAsync<IOException>(() => UpdateDiscovery.FetchOfficialTargetAsync(http, "0.17.4", NexusVersion.Stable(0, 17, 3), CancellationToken.None));
        release["published_at"] = "2026-10-14T00:00:00Z";
        assets[0]!["browser_download_url"] = "https://github.com/other/project/releases/download/v0.17.4/" + prefix + "Setup.exe";
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateDiscovery.FetchOfficialTargetAsync(http, "0.17.4", NexusVersion.Stable(0, 17, 3), CancellationToken.None));
    }

    [Fact]
    public async Task RecipeDiscoveryRequiresExactlyThreeAssetsAndDoesNotFetchAnExternalManifest()
    {
        string prefix = "NexusPipeline-v0.17.5-win-x64-";
        _entry["schemaVersion"] = 2;
        _entry.Remove("bridges");
        _entry["channels"]!["prerelease"]!["previousPublishedTarget"] = null;
        _entry["channels"]!["prerelease"]!["pendingTarget"] = new JsonObject {
            ["releaseTag"] = "v0.17.5", ["readerProtocol"] = 2,
            ["requiredCapabilities"] = new JsonArray(UpdateRecipe.Capabilities.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["basePayloadManifestSha256"] = new string('a', 64),
            ["assets"] = new JsonObject { ["setup"] = prefix + "Setup.exe", ["portable"] = prefix + "Portable.zip", ["update"] = prefix + "Update.zip" } };
        _releases["0.17.5"]["assets"] = new JsonArray(Asset(prefix + "Setup.exe", 501, [1]),
            Asset(prefix + "Portable.zip", 502, [2]), Asset(prefix + "Update.zip", 503, [3]));
        var result = await CheckAsync();
        Assert.Equal("0.17.5", result.Release?.VersionText);
        Assert.NotNull(result.Release?.DeltaAsset);
        Assert.Null(result.Release?.TargetManifestAsset);
        Assert.DoesNotContain(_requests, path => path.EndsWith("-update.json", StringComparison.Ordinal));
        ((JsonArray)_releases["0.17.5"]["assets"]!).Add(Asset("extra.zip", 504, [4]));
        Assert.Null((await CheckAsync()).Release);
    }

    [Fact]
    public async Task UnsupportedTargetUsesOnlyVerifiedCompatibleBridgeAndNeverFetchesFutureManifest()
    {
        _entry["channels"]!["prerelease"]!["previousPublishedTarget"] = null;
        var future = _entry["channels"]!["prerelease"]!["pendingTarget"]!;
        future["readerProtocol"] = 2;
        future["requiredCapabilities"] = new JsonArray("future-schema-v2");
        ((JsonArray)_entry["bridges"]!).Add(BridgeTarget("0.17.4"));
        var result = await CheckAsync();
        Assert.Equal("0.17.4", result.Release?.VersionText);
        Assert.Equal("v0.17.5", result.PendingVersion);
        Assert.Equal("bridge-required", result.PendingStatus);
        Assert.NotNull(result.Release?.FrozenTarget);
        Assert.DoesNotContain(_requests, path => path.Contains("0.17.5", StringComparison.Ordinal));
        _requests.Clear();
        var installed = await CheckAsync("0.17.4");
        Assert.Null(installed.Release);
        Assert.Equal("unsupported-capability", installed.PendingStatus);
        Assert.Single(_requests);
    }

    [Fact]
    public async Task MissingBridgePreservesPreviousAndUnmatchedCapabilitiesCannotTriggerDownloads()
    {
        AddRelease("0.17.6", 6);
        _entry["channels"]!["prerelease"]!["pendingTarget"] = Target("0.17.7");
        _entry["channels"]!["prerelease"]!["pendingTarget"]!["readerProtocol"] = 2;
        _entry["channels"]!["prerelease"]!["pendingTarget"]!["requiredCapabilities"] = new JsonArray("future-schema-v2");
        var bridge = BridgeTarget("0.17.6");
        ((JsonArray)_entry["bridges"]!).Add(bridge);
        _intercept = uri => uri.AbsolutePath.EndsWith("/tags/v0.17.6", StringComparison.Ordinal) ? new(HttpStatusCode.NotFound) : null;
        var missing = await CheckAsync();
        Assert.Equal("0.17.4", missing.Release?.VersionText);
        Assert.Equal("bridge-unavailable", missing.PendingStatus);
        foreach (string fault in new[] { "source", "destination", "entry", "loop" })
        {
            var altered = bridge.DeepClone();
            if (fault == "source") altered["sourceCapabilities"] = new JsonArray("unavailable-source");
            if (fault == "destination") altered["targetCapabilities"] = new JsonArray("unrelated-capability");
            if (fault == "entry") altered["entryTarget"]!["readerProtocol"] = 2;
            if (fault == "loop") altered["entryTarget"] = Target("0.17.7");
            _entry["bridges"] = new JsonArray(altered);
            _requests.Clear();
            var result = await CheckAsync();
            Assert.Equal("0.17.4", result.Release?.VersionText);
            Assert.Equal("unsupported-capability", result.PendingStatus);
            Assert.DoesNotContain(_requests, path => path.Contains("0.17.6", StringComparison.Ordinal) || path.Contains("0.17.7", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task BridgeDeclarationsRejectUnknownFieldsDuplicatesAndUnboundedListsBeforeLookup()
    {
        var bridge = BridgeTarget("0.17.4");
        foreach (string fault in new[] { "field", "duplicate", "count", "capability" })
        {
            var altered = bridge.DeepClone();
            if (fault == "field") altered["command"] = "not-executable";
            if (fault == "capability") altered["sourceCapabilities"] = new JsonArray("same", "same");
            var entries = new JsonArray(altered);
            if (fault == "duplicate") entries.Add(bridge.DeepClone());
            if (fault == "count") for (int index = 0; index < 8; index++) entries.Add(bridge.DeepClone());
            _entry["bridges"] = entries;
            _requests.Clear();
            await Assert.ThrowsAsync<InvalidDataException>(() => CheckAsync());
            Assert.Single(_requests);
        }
    }

    private static JsonObject BridgeTarget(string version) => new()
    {
        ["sourceReaderProtocol"] = 1,
        ["sourceCapabilities"] = new JsonArray(UpdateTargetManifest.Capabilities.Select(cap => (JsonNode?)JsonValue.Create(cap)).ToArray()),
        ["targetReaderProtocol"] = 2, ["targetCapabilities"] = new JsonArray("future-schema-v2"),
        ["entryTarget"] = Target(version)
    };

    [Fact]
    public async Task PendingRequiresPublishedReleaseAndVerifiedManifestWithIndependentPreviousFallback()
    {
        foreach (string fault in new[] { "404", "draft", "assets", "digest", "manifest", "channel" })
        {
            _requests.Clear();
            _intercept = uri =>
            {
                if (fault == "404" && uri.AbsolutePath.EndsWith("/tags/v0.17.5", StringComparison.Ordinal)) return new(HttpStatusCode.NotFound);
                if (fault == "digest" && uri.AbsolutePath.EndsWith("v0.17.5-win-x64-update.json", StringComparison.Ordinal)) return Bytes(Encoding.UTF8.GetBytes("{}"));
                if (fault == "manifest" && uri.AbsolutePath.EndsWith("v0.17.5-win-x64-update.json", StringComparison.Ordinal))
                    return Bytes(_manifests["0.17.4"]);
                if (uri.AbsolutePath.EndsWith("/tags/v0.17.5", StringComparison.Ordinal) && fault is "draft" or "channel")
                {
                    var release = _releases["0.17.5"].DeepClone();
                    release[fault == "draft" ? "draft" : "prerelease"] = fault == "draft";
                    return Json(release);
                }
                if (fault == "assets" && uri.AbsolutePath.EndsWith("/5/assets", StringComparison.Ordinal)) return Json(new JsonArray());
                return null;
            };
            var result = await CheckAsync();
            Assert.Equal("0.17.4", result.Release?.VersionText);
            Assert.NotEqual("ready", result.PendingStatus);
            Assert.DoesNotContain(_requests, path => path.EndsWith("/releases", StringComparison.Ordinal));
        }
        _intercept = null;
        var ready = await CheckAsync();
        Assert.Equal("0.17.5", ready.Release?.VersionText);
        Assert.Equal("ready", ready.PendingStatus);
        Assert.NotNull(ready.Release?.FrozenTarget);
    }

    [Fact]
    public async Task UnknownRequiredCapabilityKeepsPreviousWithoutFetchingUnsupportedTarget()
    {
        _entry["channels"]!["prerelease"]!["pendingTarget"]!["requiredCapabilities"] = new JsonArray("future-capability");
        var result = await CheckAsync();
        Assert.Equal("0.17.4", result.Release?.VersionText);
        Assert.Equal("unsupported-capability", result.PendingStatus);
        Assert.DoesNotContain(_requests, path => path.Contains("0.17.5", StringComparison.Ordinal));
        _entry["channels"]!["prerelease"]!["pendingTarget"]!["readerProtocol"] = 2;
        Assert.Equal("unsupported-capability", (await CheckAsync()).PendingStatus);
    }

    [Fact]
    public async Task FirstProtocolHasNoInventedPreviousAndNeverOffersDowngrade()
    {
        _entry["channels"]!["prerelease"]!["previousPublishedTarget"] = null;
        _intercept = uri => uri.AbsolutePath.EndsWith("/tags/v0.17.5", StringComparison.Ordinal) ? new(HttpStatusCode.NotFound) : null;
        var missing = await CheckAsync();
        Assert.Null(missing.Release);
        Assert.Equal("not-published", missing.PendingStatus);
        _intercept = null;
        _requests.Clear();
        var older = await CheckAsync("0.17.6");
        Assert.Null(older.Release);
        Assert.Equal("current-or-older", older.PendingStatus);
        Assert.Single(_requests);
    }

    [Fact]
    public async Task AssetPaginationIsBoundedAndRejectsDuplicateIdentitiesAcrossPages()
    {
        JsonArray many = (JsonArray)_releases["0.17.5"]["assets"]!.DeepClone();
        while (many.Count < 100) many.Add(Asset("extra-" + many.Count, 1000 + many.Count, [1]));
        _intercept = uri => uri.AbsolutePath.EndsWith("/5/assets", StringComparison.Ordinal)
            ? Json(uri.Query.Contains("page=1", StringComparison.Ordinal) ? many : new JsonArray(many[0]!.DeepClone())) : null;
        var duplicate = await CheckAsync();
        Assert.Equal("0.17.4", duplicate.Release?.VersionText);
        Assert.Equal("invalid-target", duplicate.PendingStatus);
        Assert.Contains(_requests, path => path.EndsWith("/5/assets?per_page=100&page=2", StringComparison.Ordinal));
        _intercept = uri => uri.AbsolutePath.EndsWith("/5/assets", StringComparison.Ordinal) ? Json(many) : null;
        Assert.Equal("0.17.4", (await CheckAsync()).Release?.VersionText);
    }

    [Fact]
    public async Task AmbiguousEnvelopeAndUnboundedMetadataFailBeforeReleaseLookup()
    {
        foreach (byte[] bytes in new[] { Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"schemaVersion\":1}"),
            Encoding.UTF8.GetBytes(_entry.ToJsonString().Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"command\":\"ignored\"", StringComparison.Ordinal)),
            new byte[2 * 1024 * 1024 + 1] })
        {
            _requests.Clear();
            _intercept = uri => uri.Host == "raw.githubusercontent.com" ? Bytes(bytes) : null;
            await Assert.ThrowsAsync<InvalidDataException>(() => CheckAsync());
            Assert.Single(_requests);
        }
    }

    [Fact]
    public async Task CallerCancellationCannotBeReportedAsPendingFailureOrPreviousSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        _intercept = uri =>
        {
            if (uri.AbsolutePath.EndsWith("/tags/v0.17.5", StringComparison.Ordinal)) cancellation.Cancel();
            return null;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CheckAsync(token: cancellation.Token));
    }

    private async Task<UpdateDiscoveryResult> CheckAsync(string current = "0.17.3", CancellationToken token = default)
    {
        using var http = new HttpClient(new Handler((request, cancellation) =>
        {
            Uri uri = request.RequestUri!;
            _requests.Add(uri.AbsolutePath + uri.Query);
            var result = _intercept?.Invoke(uri);
            cancellation.ThrowIfCancellationRequested();
            if (result is not null) return result;
            if (uri.Host == "raw.githubusercontent.com") return Json(_entry);
            foreach (var release in _releases)
            {
                if (uri.AbsolutePath.EndsWith("/tags/v" + release.Key, StringComparison.Ordinal)) return Json(release.Value);
                if (uri.AbsolutePath.EndsWith($"/{release.Value["id"]}/assets", StringComparison.Ordinal)) return Json(release.Value["assets"]!);
                if (uri.AbsolutePath.EndsWith($"v{release.Key}-win-x64-update.json", StringComparison.Ordinal)) return Bytes(_manifests[release.Key]);
            }
            throw new InvalidOperationException("Unexpected request: " + uri.AbsolutePath);
        }));
        Assert.True(NexusVersion.TryParse(current, out var version));
        return await UpdateDiscovery.FetchAsync(http, new UpdateSourcePolicy(null), "prerelease", version, token);
    }

    private void AddRelease(string version, long id)
    {
        string prefix = $"NexusPipeline-v{version}-win-x64";
        string hash = new('a', 64);
        var components = new JsonArray();
        var assets = new JsonArray(Asset(prefix + ".zip", id * 100, [1]), Asset(prefix + "-setup.exe", id * 100 + 1, [2]));
        foreach (string component in UpdateTargetManifest.ComponentIds)
        {
            string name = prefix + "-" + component + ".zip";
            components.Add(new JsonObject { ["id"] = component, ["assetName"] = name });
            assets.Add(Asset(name, id * 100 + assets.Count, [3]));
        }
        var files = new JsonArray();
        foreach (string path in new[] { "NexusPipeline.exe", "README.md", ApplicationPayload.ManifestPath, ApplicationPayload.DesktopEntry, ApplicationPayload.DesktopArchive })
            files.Add(new JsonObject { ["path"] = path, ["sha256"] = hash, ["sizeBytes"] = 1,
                ["componentId"] = path == ApplicationPayload.DesktopEntry ? "desktop-runtime" : path == ApplicationPayload.DesktopArchive ? "desktop-app" : "host-core" });
        var manifest = new JsonObject { ["schemaVersion"] = 1, ["product"] = "NexusPipeline",
            ["target"] = new JsonObject { ["version"] = version, ["rid"] = "win-x64", ["installationGeneration"] = "g0170", ["buildId"] = hash, ["frontendHash"] = hash, ["payloadManifestSha256"] = hash },
            ["requirements"] = new JsonObject { ["readerProtocol"] = 1, ["runnerHandshakeProtocol"] = 1, ["requiredCapabilities"] = new JsonArray(UpdateTargetManifest.Capabilities.Select(cap => (JsonNode?)JsonValue.Create(cap)).ToArray()) },
            ["fullPackage"] = new JsonObject { ["assetName"] = prefix + ".zip" }, ["components"] = components, ["files"] = files,
            ["migration"] = new JsonObject { ["routeId"] = "g0170-no-store-change-v1", ["sourceGeneration"] = "g0170", ["targetGeneration"] = "g0170", ["stores"] = new JsonArray(), ["migratorIds"] = new JsonArray(), ["runner"] = new JsonObject { ["kind"] = "target-host", ["entryPath"] = "NexusPipeline.exe" } } };
        _manifests.Add(version, Encoding.UTF8.GetBytes(manifest.ToJsonString()));
        assets.Add(Asset(prefix + "-update.json", id * 100 + assets.Count, _manifests[version]));
        _releases.Add(version, new JsonObject { ["id"] = id, ["tag_name"] = "v" + version, ["draft"] = false, ["prerelease"] = true, ["assets"] = assets });
    }
    private static JsonObject Target(string version) => new() { ["releaseTag"] = "v" + version,
        ["manifestAssetName"] = $"NexusPipeline-v{version}-win-x64-update.json", ["readerProtocol"] = 1,
        ["requiredCapabilities"] = new JsonArray(UpdateTargetManifest.Capabilities.Select(cap => (JsonNode?)JsonValue.Create(cap)).ToArray()) };
    private static JsonObject Asset(string name, long id, byte[] bytes) => new() { ["id"] = id, ["name"] = name,
        ["state"] = "uploaded", ["size"] = bytes.Length, ["digest"] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)),
        ["browser_download_url"] = "https://github.com/FlappiBakuse/NexusPipeline/releases/download/fixture/" + name };
    private static HttpResponseMessage Json(JsonNode node) => Bytes(Encoding.UTF8.GetBytes(node.ToJsonString()));
    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(callback(request, cancellationToken));
    }
}
