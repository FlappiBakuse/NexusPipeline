using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class BoundPackageFixture : IDisposable
{
    internal readonly ExtensionCatalogFixture Catalog;
    internal readonly Guid OperationId = Guid.NewGuid();
    internal readonly string Root, Archive, Deployed;
    internal readonly byte[] ManifestBytes;
    private readonly JsonObject _original;
    private readonly ExtensionStoreChannel _channel;

    internal BoundPackageFixture(string archive, JsonObject manifest, byte[] manifestBytes,
        ExtensionCatalogFixture? catalog, ExtensionStoreChannel channel)
    {
        Catalog = catalog ?? new(); _channel = channel; _original = manifest.DeepClone().AsObject();
        ManifestBytes = manifestBytes.ToArray();
        if (!JsonNode.DeepEquals(JsonNode.Parse(ManifestBytes), _original)) throw new IOException("fixture_original_manifest_changed");
        Root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, Guid.NewGuid().ToString("N"));
        Deployed = Path.Combine(Root, "deployed"); Directory.CreateDirectory(Deployed);
        Archive = Path.Combine(Root, "package.zip"); File.Copy(archive, Archive);
        using var package = ZipFile.OpenRead(Archive);
        using var input = (package.GetEntry("manifest.json") ?? throw new IOException("fixture_package_manifest_missing")).Open();
        using var bytes = new MemoryStream(); input.CopyTo(bytes);
        if (!bytes.ToArray().AsSpan().SequenceEqual(ManifestBytes)) throw new IOException("fixture_package_manifest_changed");
    }

    internal BoundPackageFixture(string kind = "managed-code", Action<JsonObject>? manifestEdit = null, Action<string>? payloadEdit = null, ExtensionCatalogFixture? catalog = null, JsonObject? originalManifest = null, byte[]? originalManifestBytes = null, ExtensionStoreChannel channel = ExtensionStoreChannel.Stable)
    {
        Catalog = catalog ?? new(); _channel = channel;
        Root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, Guid.NewGuid().ToString("N"));
        Deployed = Path.Combine(Root, "deployed"); Directory.CreateDirectory(Deployed);
        _original = new JsonObject
        {
            ["format"] = "nexus.extension.manifest", ["version"] = 1,
            ["extensionId"] = "fixture-plugin", ["artifactName"] = "FixturePlugin", ["displayName"] = "Controlled unactivated fixture",
            ["packageVersion"] = "0.5.0", ["kind"] = kind, ["minHostVersion"] = "0.18.0",
            ["sdkVersion"] = SdkIdentity.Version, ["contractFingerprint"] = SdkIdentity.ContractFingerprint,
            ["capabilities"] = new JsonArray(), ["permissions"] = new JsonObject()
        };
        if (originalManifest is not null) _original = originalManifest.DeepClone().AsObject();
        if (kind == "managed-code")
        {
            if (originalManifest is null) _original["managed"] = new JsonObject { ["entryAssembly"] = "entry.dll", ["entryType"] = "UnactivatedFixture" };
            File.Copy(typeof(ExtensionCatalogFixture).Assembly.Location, Path.Combine(Deployed, "entry.dll"));
        }
        else
        {
            if (originalManifest is null)
            {
                _original["specialized"] = new JsonObject { ["adapterEntry"] = "adapter/main.js" };
                Directory.CreateDirectory(Path.Combine(Deployed, "adapter"));
                File.WriteAllText(Path.Combine(Deployed, "adapter", "main.js"), "return {};");
            }
        }
        payloadEdit?.Invoke(Deployed);
        var actual = _original.DeepClone().AsObject(); manifestEdit?.Invoke(actual);
        if (originalManifestBytes is not null && !JsonNode.DeepEquals(JsonNode.Parse(originalManifestBytes), actual))
            throw new IOException("fixture_original_manifest_changed");
        ManifestBytes = originalManifestBytes?.ToArray() ?? JsonSerializer.SerializeToUtf8Bytes(actual);
        File.WriteAllBytes(Path.Combine(Deployed, "manifest.json"), ManifestBytes);
        Archive = Path.Combine(Root, "package.zip");
        ZipFile.CreateFromDirectory(Deployed, Archive, CompressionLevel.NoCompression, includeBaseDirectory: false);
    }

    internal SignedCatalogBytes Signed(long sequence = 1, Action<JsonObject>? entryEdit = null) => Catalog.Signed(sequence, channel: _channel == ExtensionStoreChannel.Stable ? "stable" : "develop", edit: document =>
    {
        var entry = document["entries"]![0]!.AsObject();
        foreach (var name in new[] { "extensionId", "artifactName", "packageVersion", "kind", "minHostVersion", "sdkVersion" })
            entry[name] = _original[name]!.DeepClone();
        if (_original["metadata"] is { } metadata) entry["metadata"] = metadata.DeepClone();
        else entry.Remove("metadata");
        entry["manifestSha256"] = Convert.ToHexStringLower(SHA256.HashData(ManifestBytes));
        entry["artifact"]!["sizeBytes"] = new FileInfo(Archive).Length;
        entry["artifact"]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Archive)));
        entryEdit?.Invoke(entry);
    });

    internal void Publish(long sequence = 1, Action<JsonObject>? entryEdit = null)
    {
        var signed = Signed(sequence, entryEdit);
        _ = Catalog.Store.Accept(Guid.NewGuid(), "local", Catalog.Verify(signed, _channel));
    }

    internal ResourceOperationLease Acquire(Guid? operation = null, bool catalogClaim = true)
    {
        var claims = new List<ResourceClaim> { new("blobs:content", ResourceClaimMode.Reference) };
        if (catalogClaim) claims.Add(new("extensions:catalog", ResourceClaimMode.Reference));
        return Catalog.Resources.TryAcquire(operation ?? OperationId, claims).Lease ?? throw new IOException("fixture_resources_unavailable");
    }

    internal VerifiedExtensionPackage Prepare(ResourceOperationLease root) => VerifiedExtensionPackage.Prepare(Catalog.Store,
        _channel, OperationId, root, _original["extensionId"]!.GetValue<string>(), Archive, Catalog.Contracts, CancellationToken.None);
    internal ImmutableBlobStore Blobs => new(Catalog.Instance, Catalog.Contracts);
    internal string Fingerprint() => string.Join("\n", Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal).Select(path =>
            Path.GetRelativePath(Root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))));
    public void Dispose() => Catalog.Dispose();
}
