using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Shared.Versioning;
using Xunit;

namespace NexusPipeline.Tests.Updates;

public sealed class UpdateRecipeDeliveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nxp-recipe-delivery-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _packages = new(StringComparer.Ordinal);
    private readonly List<string> _requests = [];
    private readonly UpdatePayloadIdentity _old = new(new string('d', 64), new string('e', 64), new string('f', 64), "g0170");
    private readonly UpdatePayloadIdentity _target;
    private readonly ReleaseInfo _release;
    private bool _failDelta;
    private string Installation => Path.Combine(_root, "install");
    private string Staging => Path.Combine(_root, "staging");

    public UpdateRecipeDeliveryTests()
    {
        Directory.CreateDirectory(Installation);
        foreach (string name in new[] { "NexusPipeline.exe", UpdateRecipe.UpdateEntry, "README.md", ApplicationPayload.ManifestPath,
            ApplicationPayload.DesktopEntry, ApplicationPayload.DesktopArchive,
            "resources/desktop/resources/app.asar.unpacked/node_modules/@koromix/koffi-win32-x64/win32_x64/koffi.node" })
        {
            _files[name] = name == ApplicationPayload.DesktopEntry ? RandomNumberGenerator.GetBytes(64 * 1024) : Encoding.UTF8.GetBytes(name);
            string path = Path.Combine(Installation, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, _files[name]);
        }
        Directory.CreateDirectory(Path.Combine(Installation, "config"));
        File.WriteAllText(Path.Combine(Installation, "config/user.json"), "unchanged-user-data");
        Directory.CreateDirectory(Path.Combine(Installation, "plugins/LiveScreenshot"));
        File.WriteAllText(Path.Combine(Installation, "plugins/LiveScreenshot/plugin.json"), "installed-plugin");
        _target = new(Hash(_files[ApplicationPayload.ManifestPath]), new string('a', 64), new string('b', 64), "g0170");
        string prefix = "NexusPipeline-v0.17.4-win-x64-";
        var assets = new Dictionary<string, UpdateReleaseAsset>();
        UpdateReleaseAsset Asset(string name, byte[] raw)
        {
            _packages[name] = raw;
            var value = new UpdateReleaseAsset(assets.Count + 1, name, "https://fixture.example/" + name, raw.Length, Hash(raw));
            assets[name] = value;
            return value;
        }
        var full = Asset(prefix + "Portable.zip", Package(false));
        var delta = Asset(prefix + "Update.zip", Package(true));
        Asset(prefix + "Setup.exe", [1]);
        Assert.True(delta.SizeBytes < full.SizeBytes);
        NexusVersion.TryParse("0.17.4", out var version);
        _release = new(version, "v0.17.4", "", "", true, 3, full, Assets: assets, DeltaAsset: delta, BaseManifestHint: _old.ManifestSha256);
    }

    private byte[] Package(bool delta)
    {
        bool Reused(string name) => delta && name == ApplicationPayload.DesktopEntry;
        JsonObject FileItem(KeyValuePair<string, byte[]> item) => new() { ["path"] = item.Key, ["sizeBytes"] = item.Value.Length, ["sha256"] = Hash(item.Value) };
        var recipe = new JsonObject {
            ["schemaVersion"] = 1, ["packageKind"] = delta ? "update" : "portable", ["product"] = "NexusPipeline",
            ["rid"] = "win-x64", ["installationGeneration"] = "g0170",
            ["target"] = new JsonObject { ["version"] = "0.17.4", ["buildId"] = _target.BuildId,
                ["frontendHash"] = _target.FrontendHash, ["payloadManifestSha256"] = _target.ManifestSha256 },
            ["base"] = delta ? new JsonObject { ["releaseTag"] = "v0.17.3", ["releaseId"] = 1, ["assetId"] = 2,
                ["assetSha256"] = new string('c', 64), ["payloadManifestSha256"] = _old.ManifestSha256 } : null,
            ["requirements"] = new JsonObject { ["readerProtocol"] = 2, ["routeId"] = "g0170-no-store-change-v1",
                ["requiredCapabilities"] = new JsonArray(UpdateRecipe.Capabilities.Select(cap => (JsonNode?)JsonValue.Create(cap)).ToArray()) },
            ["includedFiles"] = new JsonArray(_files.Where(file => !Reused(file.Key)).Select(file => (JsonNode?)FileItem(file)).ToArray()),
            ["reusedFiles"] = new JsonArray(_files.Where(file => Reused(file.Key)).Select(file => (JsonNode?)FileItem(file)).ToArray()),
            ["removedFiles"] = new JsonArray(), ["bundledFiles"] = delta ? new JsonArray() : new JsonArray(FileItem(new("plugins/LiveScreenshot/plugin.json", [1, 2, 3]))),
            ["manualCover"] = new JsonObject { ["allowed"] = delta, ["minimumSourceVersion"] = "0.17.3",
                ["exactBaseRequired"] = delta, ["requiresPostStartReconciliation"] = true, ["reason"] = "fixture" } };
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            foreach (var file in _files.Where(file => !Reused(file.Key)))
            { using var stream = archive.CreateEntry(file.Key).Open(); stream.Write(file.Value); }
            if (!delta) { using var stream = archive.CreateEntry("plugins/LiveScreenshot/plugin.json").Open(); stream.Write([1, 2, 3]); }
            foreach (string name in UpdateRecipe.Guides.Append(UpdateRecipe.RecipePath))
            { using var stream = new StreamWriter(archive.CreateEntry(name).Open()); stream.Write(name == UpdateRecipe.RecipePath ? recipe.ToJsonString() : "Back up before manual copying."); }
        }
        return bytes.ToArray();
    }

    private async Task<UpdateDeliveryResult> Prepare(ReleaseInfo? release = null)
    {
        using var http = new HttpClient(new Handler(request => {
            string name = request.RequestUri!.Segments[^1]; _requests.Add(name);
            return _failDelta && name.EndsWith("Update.zip", StringComparison.Ordinal)
                ? new(HttpStatusCode.ServiceUnavailable) : new(HttpStatusCode.OK) { Content = new ByteArrayContent(_packages[name]) };
        }));
        UpdatePayloadIdentity Freeze(string root, string? version, bool installed)
        {
            if (installed) return _old;
            foreach (var file in _files) Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(root, file.Key)));
            return _target;
        }
        return await UpdateDelivery.PrepareAsync(http, new UpdateSourcePolicy("https://fixture.example/releases"), release ?? _release,
            Installation, "0.17.3", Path.Combine(_root, "target.zip"), Path.Combine(_root, "receipt"), Staging,
            Freeze, new UpdateCreatedArtifacts(_root), null, CancellationToken.None);
    }

    [Fact]
    public async Task DeltaReconstructsExactFilesWithoutTransportMetadataOrUserDataWrites()
    {
        var result = await Prepare();
        Assert.Equal("delta", result.Transport);
        Assert.Equal(_release.DeltaAsset!.SizeBytes, result.NetworkBytes);
        Assert.Equal(_files[ApplicationPayload.DesktopEntry].Length, result.ReusedBytes);
        Assert.Single(_requests);
        Assert.False(Directory.Exists(Path.Combine(Staging, "package-info")));
        Assert.Equal("unchanged-user-data", File.ReadAllText(Path.Combine(Installation, "config/user.json")));
    }

    [Fact]
    public async Task DifferentBaselineSelectsOnlyPortableBeforeDownloading()
    {
        var result = await Prepare(_release with { BaseManifestHint = new string('0', 64) });
        Assert.Equal("full", result.Transport);
        Assert.Equal([_release.ZipAsset.Name], _requests);
        Assert.False(Directory.Exists(Path.Combine(Staging, "plugins")));
        Assert.Equal("installed-plugin", File.ReadAllText(Path.Combine(Installation, "plugins/LiveScreenshot/plugin.json")));
    }

    [Fact]
    public async Task MissingReadmeSelectsPortableAndRestoresOnlyStagedApplication()
    {
        File.Delete(Path.Combine(Installation, "README.md"));
        var result = await Prepare();
        Assert.Equal("full", result.Transport);
        Assert.Equal([_release.ZipAsset.Name], _requests);
        Assert.False(File.Exists(Path.Combine(Installation, "README.md")));
        Assert.Equal(_files["README.md"], File.ReadAllBytes(Path.Combine(Staging, "README.md")));
    }

    [Fact]
    public async Task TransportFailureFallsBackOnce()
    {
        _failDelta = true;
        var result = await Prepare();
        Assert.Equal("full", result.Transport);
        Assert.Equal([_release.DeltaAsset!.Name, _release.ZipAsset.Name], _requests);
    }

    [Fact]
    public async Task DigestFailureCannotTriggerPortableFallback()
    {
        _packages[_release.DeltaAsset!.Name][0] ^= 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => Prepare());
        Assert.Equal([_release.DeltaAsset.Name], _requests);
        Assert.False(Directory.Exists(Staging));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(send(request));
    }
}
