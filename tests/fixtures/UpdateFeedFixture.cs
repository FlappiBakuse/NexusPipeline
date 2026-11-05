using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Maintenance;

namespace NexusPipeline.Tests.Fixtures;

internal sealed class UpdateFeedFixture : IDisposable
{
    internal readonly ECDsa Signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    internal readonly ContractReader Contracts = new();
    internal static readonly Uri FeedRoot = new("https://updates.example.test/metadata/update/1/");
    internal static readonly Uri StoreRoot = new("https://store.example.test/metadata/store/1/");
    internal static readonly Uri Artifacts = new("https://assets.example.test/releases/download/");

    internal UpdateFeedVerifier Verifier(byte[]? publicKey = null, long floor = 1) => new(Contracts,
        new UpdateTrustPolicy(new Dictionary<string, byte[]> { ["fixture-only"] = publicKey ?? Signer.ExportSubjectPublicKeyInfo() },
            FeedRoot, StoreRoot, [Artifacts], floor, floor, new(0, 18, 0)));

    internal JsonObject Document(long sequence = 1, string channel = "stable", string version = "0.18.0")
    {
        var generation = Guid.NewGuid().ToString("D");
        var prefix = new Uri(StoreRoot, "stable/generations/" + generation + "/");
        return new JsonObject
        {
            ["format"] = "nexus.update.feed", ["version"] = 1, ["channel"] = channel, ["sequence"] = sequence,
            ["releases"] = new JsonArray(new JsonObject
            {
                ["hostVersion"] = version, ["minReaderVersion"] = "0.18.0", ["releaseId"] = 1, ["assetId"] = 2,
                ["artifact"] = new JsonObject { ["url"] = new Uri(Artifacts, "host/" + version + "/Host.zip").AbsoluteUri,
                    ["sizeBytes"] = 1024, ["sha256"] = new string('a', 64) },
                ["payloadManifestSha256"] = new string('b', 64), ["publishedAt"] = "2026-10-22T00:00:00.0000000Z",
                ["storeBinding"] = new JsonObject { ["channel"] = "stable", ["generationId"] = generation, ["sequence"] = 1,
                    ["catalogUrl"] = new Uri(prefix, "catalog.json").AbsoluteUri, ["catalogSha256"] = new string('c', 64),
                    ["signatureUrl"] = new Uri(prefix, "catalog.sig.json").AbsoluteUri }
            })
        };
    }

    internal (byte[] Feed, byte[] Signature) Signed(JsonObject document)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document);
        return (bytes, Signature(bytes));
    }

    internal byte[] Signature(byte[] bytes) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        format = "nexus.detached.signature", version = 1, keyId = "fixture-only",
        algorithm = "ecdsa-p256-sha256", encoding = "base64-p1363",
        signature = Convert.ToBase64String(Signer.SignData(bytes, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
    });
    public void Dispose() => Signer.Dispose();
}
