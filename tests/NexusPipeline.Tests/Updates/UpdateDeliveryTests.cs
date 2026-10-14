using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Shared.Versioning;
using Xunit;

namespace NexusPipeline.Tests.Updates;

public sealed class UpdateDeliveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nxp-components-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _assets = new(StringComparer.Ordinal);
    private readonly List<string> _requests = [];
    private readonly ReleaseInfo _release;
    private readonly JsonObject _manifest;
    private readonly UpdatePayloadIdentity _identity;
    private Func<string, HttpResponseMessage?>? _intercept;
    private string Installation => Path.Combine(_root, "install");
    private string UpdateRoot => Path.Combine(Installation, ".nxp-update");
    private string ZipPath => Path.Combine(UpdateRoot, "target.zip");
    private string ChecksumPath => Path.Combine(UpdateRoot, "target.receipt");
    private string Staging => Path.Combine(UpdateRoot, "staging");

    public UpdateDeliveryTests()
    {
        Directory.CreateDirectory(Installation);
        _files.Add("NexusPipeline.exe", Encoding.UTF8.GetBytes("new-host"));
        _files.Add("README.md", Encoding.UTF8.GetBytes("new-readme"));
        _files.Add(ApplicationPayload.ManifestPath, Encoding.UTF8.GetBytes("{\"fixture\":true}"));
        _files.Add(ApplicationPayload.DesktopArchive, Encoding.UTF8.GetBytes("new-desktop-app"));
        _files.Add(ApplicationPayload.DesktopEntry, RandomNumberGenerator.GetBytes(32 * 1024));
        foreach (var file in _files)
        {
            string path = Path.Combine(Installation, file.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, file.Key == ApplicationPayload.DesktopEntry ? file.Value : Encoding.UTF8.GetBytes("original"));
        }
        Directory.CreateDirectory(Path.Combine(Installation, "config"));
        File.WriteAllText(Path.Combine(Installation, "config/user.json"), "user-data");
        File.WriteAllText(Path.Combine(Installation, "unknown.txt"), "unknown-data");
        const string version = "0.17.4-beta.1";
        string prefix = $"NexusPipeline-v{version}-win-x64";
        string Owner(string name) => name == ApplicationPayload.DesktopEntry ? "desktop-runtime"
            : name == ApplicationPayload.DesktopArchive ? "desktop-app" : "host-core";
        var assets = new Dictionary<string, UpdateReleaseAsset>(StringComparer.Ordinal);
        UpdateReleaseAsset Asset(string name, byte[] bytes)
        {
            _assets.Add('/' + name, bytes);
            var asset = new UpdateReleaseAsset(100 + assets.Count, name, "http://127.0.0.1:9876/" + name, bytes.Length, Hash(bytes));
            assets.Add(name, asset);
            return asset;
        }
        var zip = Asset(prefix + ".zip", Archive(_files));
        var components = new JsonArray();
        foreach (string id in UpdateTargetManifest.ComponentIds)
        {
            string name = prefix + "-" + id + ".zip";
            Asset(name, Archive(_files.Where(item => Owner(item.Key) == id).ToDictionary()));
            components.Add(new JsonObject { ["id"] = id, ["assetName"] = name });
        }
        _identity = new(Hash(_files[ApplicationPayload.ManifestPath]), new string('b', 64), new string('c', 64), "g0170");
        _manifest = new JsonObject
        {
            ["schemaVersion"] = 1, ["product"] = "NexusPipeline",
            ["target"] = new JsonObject { ["version"] = version, ["rid"] = "win-x64", ["installationGeneration"] = "g0170",
                ["buildId"] = _identity.BuildId, ["frontendHash"] = _identity.FrontendHash, ["payloadManifestSha256"] = _identity.ManifestSha256 },
            ["requirements"] = new JsonObject { ["readerProtocol"] = 1, ["runnerHandshakeProtocol"] = 1,
                ["requiredCapabilities"] = new JsonArray(UpdateTargetManifest.Capabilities.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()) },
            ["fullPackage"] = new JsonObject { ["assetName"] = zip.Name }, ["components"] = components,
            ["files"] = new JsonArray(_files.Select(file => (JsonNode?)new JsonObject { ["path"] = file.Key,
                ["sizeBytes"] = file.Value.Length, ["sha256"] = Hash(file.Value), ["componentId"] = Owner(file.Key) }).ToArray()),
            ["migration"] = new JsonObject { ["routeId"] = "g0170-no-store-change-v1", ["sourceGeneration"] = "g0170", ["targetGeneration"] = "g0170",
                ["stores"] = new JsonArray(), ["migratorIds"] = new JsonArray(), ["runner"] = new JsonObject { ["kind"] = "target-host", ["entryPath"] = "NexusPipeline.exe" } }
        };
        var target = Asset(prefix + "-update.json", Encoding.UTF8.GetBytes(_manifest.ToJsonString()));
        Assert.True(NexusVersion.TryParse(version, out var parsed));
        _release = new(parsed, "v" + version, "", "", true, 1, zip, target, assets);
    }

    [Fact]
    public async Task ReusesOnlyMatchingComponentsAndReconstructsTheExactTarget()
    {
        var result = await PrepareAsync();
        Assert.Equal("components", result.Transport);
        Assert.Equal(_files[ApplicationPayload.DesktopEntry].Length, result.ReusedBytes);
        Assert.DoesNotContain(_requests, value => value.EndsWith("-desktop-runtime.zip", StringComparison.Ordinal));
        Assert.DoesNotContain('/' + _release.ZipAsset.Name, _requests);
        foreach (var file in _files) Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(Staging, file.Key)));
        Assert.True(UpdatePackage.VerifySha256(ZipPath, ChecksumPath, out _));
        Assert.False(Directory.Exists(ZipPath + ".delivery"));
        AssertOriginalData();
    }

    [Fact]
    public async Task FallsBackOnceForUnavailableComponentAndNeverRetriesThatAsset()
    {
        _intercept = name => name.EndsWith("-host-core.zip", StringComparison.Ordinal) ? new(HttpStatusCode.NotFound) : null;
        var result = await PrepareAsync();
        Assert.Equal("component-transport-unavailable", result.Reason);
        Assert.Single(_requests, value => value.EndsWith("-host-core.zip", StringComparison.Ordinal));
        Assert.Single(_requests, value => value == '/' + _release.ZipAsset.Name);
        AssertOriginalData();
    }

    [Fact]
    public async Task CorruptedComponentAndInvalidBaselineCannotTriggerFullFallback()
    {
        _intercept = name => name.EndsWith("-host-core.zip", StringComparison.Ordinal)
            ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[_assets[name].Length]) } : null;
        await Assert.ThrowsAsync<InvalidDataException>(() => PrepareAsync());
        Assert.DoesNotContain('/' + _release.ZipAsset.Name, _requests);
        Assert.False(Directory.Exists(Staging));
        _requests.Clear(); _intercept = null;
        await Assert.ThrowsAsync<PayloadFileException>(() => PrepareAsync(freeze: (_, _, _) => throw new PayloadFileException("payload_file_changed", "NexusPipeline.exe")));
        Assert.Single(_requests);
        AssertOriginalData();
    }

    [Fact]
    public void UnknownProtocolRouteFieldsAndAmbiguousFileSetsFailClosed()
    {
        foreach (Action<JsonObject> mutate in new Action<JsonObject>[]
        {
            value => value["requirements"]!["readerProtocol"] = 2,
            value => value["migration"]!["routeId"] = "unregistered",
            value => value["migration"]!["runner"]!["entryPath"] = "other.exe",
            value => value["target"]!["buildId"] = "invalid",
            value => value["unknown"] = true,
            value => value["files"]!.AsArray().Add(value["files"]![0]!.DeepClone()),
            value => value["files"]![0]!["path"] = "../escape",
            value => value["files"]![0]!["sizeBytes"] = long.MaxValue,
            value => value["files"]![0]!["componentId"] = "desktop-runtime"
        })
        {
            var value = (JsonObject)_manifest.DeepClone();
            mutate(value);
            Assert.Throws<InvalidDataException>(() => UpdateTargetManifest.Parse(Encoding.UTF8.GetBytes(value.ToJsonString()), _release));
        }
    }

    [Fact]
    public async Task CancellationPreservesInstallationAndDoesNotDownloadFullPackage()
    {
        using var cancellation = new CancellationTokenSource();
        _intercept = name =>
        {
            if (name.EndsWith("-host-core.zip", StringComparison.Ordinal)) cancellation.Cancel();
            return null;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PrepareAsync(cancellation.Token));
        Assert.DoesNotContain('/' + _release.ZipAsset.Name, _requests);
        Assert.False(File.Exists(ZipPath));
        AssertOriginalData();
    }

    [Fact]
    public async Task ConflictingLocalPackageIsNeverAdoptedOrDeleted()
    {
        Directory.CreateDirectory(UpdateRoot);
        File.WriteAllText(ZipPath, "unowned-package");
        await Assert.ThrowsAsync<IOException>(() => PrepareAsync());
        Assert.Equal("unowned-package", File.ReadAllText(ZipPath));
        AssertOriginalData();
    }

    private async Task<UpdateDeliveryResult> PrepareAsync(CancellationToken token = default,
        Func<string, string?, bool, UpdatePayloadIdentity>? freeze = null)
    {
        using var http = new HttpClient(new Handler(request =>
        {
            string name = request.RequestUri!.AbsolutePath;
            _requests.Add(name);
            return _intercept?.Invoke(name) ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_assets[name]) };
        }));
        return await UpdateDelivery.PrepareAsync(http, new UpdateSourcePolicy("http://127.0.0.1:9876/releases"), _release,
            Installation, "0.17.3", ZipPath, ChecksumPath, Staging, freeze ?? ((_, _, _) => _identity),
            new UpdateCreatedArtifacts(UpdateRoot), null, token);
    }

    private void AssertOriginalData()
    {
        Assert.Equal("user-data", File.ReadAllText(Path.Combine(Installation, "config/user.json")));
        Assert.Equal("unknown-data", File.ReadAllText(Path.Combine(Installation, "unknown.txt")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(Installation, "NexusPipeline.exe")));
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] Archive(Dictionary<string, byte[]> files)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var file in files)
            {
                using var stream = archive.CreateEntry(file.Key).Open();
                stream.Write(file.Value);
            }
        return output.ToArray();
    }
    public void Dispose() => Directory.Delete(_root, true);
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
