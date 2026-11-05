using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class StoreMirrorFailures
{
    [Theory]
    [InlineData("signature")]
    [InlineData("catalog")]
    [InlineData("missing")]
    [InlineData("equivocation")]
    [InlineData("rollback")]
    [InlineData("host")]
    [InlineData("sdk")]
    public async Task PreservesAcceptedCatalog(string fault)
    {
        using var inputs = new SignedStoreInputs();
        await using var mirror = await StoreMirror.StartAsync(); inputs.Populate(mirror);
        await using var runtime = new StoreMirrorRuntime(inputs, mirror);
        await runtime.Runtime.StartAsync(default);
        var service = runtime.Service;
        var original = await service.RefreshAsync("local", ExtensionStoreChannel.Stable, default);
        using var pointer = JsonDocument.Parse(inputs.Pointer);
        var catalogUrl = pointer.RootElement.GetProperty("catalogUrl").GetString()!;
        var signatureUrl = pointer.RootElement.GetProperty("signatureUrl").GetString()!;
        switch (fault)
        {
            case "signature":
                var signature = JsonNode.Parse(inputs.Signature)!;
                signature["signature"] = Convert.ToBase64String(new byte[64]);
                mirror.Files[signatureUrl] = JsonSerializer.SerializeToUtf8Bytes(signature); break;
            case "catalog": mirror.Files[catalogUrl] = inputs.Catalog.Concat(" "u8.ToArray()).ToArray(); break;
            case "missing": Assert.True(mirror.Files.TryRemove(catalogUrl, out _)); break;
            case "host":
            case "sdk":
                Publish(runtime, inputs, mirror, original.Identity.Sequence + 1, catalog =>
                    catalog["entries"]![0]![fault == "host" ? "minHostVersion" : "sdkVersion"] = "99.0.0"); break;
            default: Publish(runtime, inputs, mirror, original.Identity.Sequence - (fault == "rollback" ? 1 : 0)); break;
        }
        if (fault is "rollback" or "equivocation")
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.RefreshAsync("local", ExtensionStoreChannel.Stable, default));
            Assert.Equal("extension_catalog_rollback_rejected", error.Message);
        }
        else await Assert.ThrowsAsync<IOException>(() => service.RefreshAsync("local", ExtensionStoreChannel.Stable, default));
        Assert.Equal(original.Identity, service.ReadCached(ExtensionStoreChannel.Stable)!.Identity);
        Assert.Empty(runtime.Runtime.ReadStates());
        inputs.VerifyUnchanged();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsDamagedPackages(bool missing)
    {
        using var inputs = new SignedStoreInputs();
        await using var mirror = await StoreMirror.StartAsync(); inputs.Populate(mirror);
        await using var runtime = new StoreMirrorRuntime(inputs, mirror);
        await runtime.Runtime.StartAsync(default);
        var catalog = await runtime.Service.RefreshAsync("local", ExtensionStoreChannel.Stable, default);
        var entry = catalog.Entries.Single(item => item.ExtensionId == "bettergi");
        using var document = JsonDocument.Parse(inputs.Catalog);
        var url = document.RootElement.GetProperty("entries").EnumerateArray()
            .Single(item => item.GetProperty("extensionId").GetString() == entry.ExtensionId)
            .GetProperty("artifact").GetProperty("url").GetString()!;
        if (missing) Assert.True(mirror.Files.TryRemove(url, out _));
        else { var bytes = mirror.Files[url].ToArray(); bytes[^1] ^= 1; mirror.Files[url] = bytes; }
        await Assert.ThrowsAsync<IOException>(() => runtime.Service.ApplyAsync(Guid.NewGuid(), "local",
            new(ExtensionApplyAction.Install, entry.ExtensionId, 0, runtime.Settings.Read().Revision, catalog.Identity, entry), default));
        Assert.Empty(runtime.Runtime.ReadStates());
        Assert.Equal(catalog.Identity, runtime.Service.ReadCached(ExtensionStoreChannel.Stable)!.Identity);
        inputs.VerifyUnchanged();
    }

    internal static void Publish(StoreMirrorRuntime runtime, SignedStoreInputs inputs, StoreMirror mirror, long sequence,
        Action<JsonNode>? edit = null)
    {
        var generation = Guid.NewGuid().ToString("D");
        var catalog = JsonNode.Parse(inputs.Catalog)!; catalog["generationId"] = generation; catalog["sequence"] = sequence;
        edit?.Invoke(catalog);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(catalog);
        var signature = JsonNode.Parse(inputs.Signature)!;
        signature["signature"] = Convert.ToBase64String(runtime.Owner.Signer.SignData(bytes, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var prefix = new Uri(ExtensionCatalogFixture.MetadataRoot, "stable/generations/" + generation + "/");
        var pointer = JsonNode.Parse(inputs.Pointer)!;
        pointer["generationId"] = generation; pointer["sequence"] = sequence;
        pointer["catalogUrl"] = new Uri(prefix, "catalog.json").AbsoluteUri;
        pointer["signatureUrl"] = new Uri(prefix, "catalog.sig.json").AbsoluteUri;
        pointer["catalogSha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
        mirror.Files[new Uri(prefix, "catalog.json").AbsoluteUri] = bytes;
        mirror.Files[new Uri(prefix, "catalog.sig.json").AbsoluteUri] = JsonSerializer.SerializeToUtf8Bytes(signature);
        mirror.Files[new Uri(ExtensionCatalogFixture.MetadataRoot, "stable/index.json").AbsoluteUri] = JsonSerializer.SerializeToUtf8Bytes(pointer);
    }
}
