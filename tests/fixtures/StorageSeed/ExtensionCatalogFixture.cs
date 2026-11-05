using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.StorageTests;

internal sealed record SignedCatalogBytes(byte[] Pointer, byte[] Catalog, byte[] Signature);

[SupportedOSPlatform("windows")]
internal sealed class ExtensionCatalogFixture : IDisposable
{
    private readonly bool _ownsLease = true;
    internal readonly ContractReader Contracts = new();
    internal readonly ECDsa Signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    internal readonly RuntimeInstance Instance;
    internal readonly StateDatabase Database;
    internal readonly InstanceLease Lease;
    internal readonly RecoveryIndex Index = new();
    internal readonly ResourceLeaseManager Resources;
    internal readonly HostLifecycle Lifecycle;
    internal readonly ExtensionCatalogVerifier Verifier;
    internal readonly ExtensionCatalogStore Store;
    internal static readonly Uri MetadataRoot = new("https://store.example.test/metadata/store/1/");
    internal static readonly Uri Artifacts = new("https://assets.example.test/releases/download/");

    internal ExtensionCatalogFixture(long stableFloor = 1, long developFloor = 1, bool useHostTestTrust = false)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ??
            throw new IOException("Explicit test root required"), Guid.NewGuid().ToString("N"));
        Instance = new InstanceRoot(Contracts).OpenOrCreate(Path.Combine(root, "runtime"));
        Database = StateDatabase.CreateFresh(Instance); Lease = InstanceLease.Acquire(Instance, Contracts);
        Index.PublishVerified(0, 1, []); Lifecycle = new(Index, TimeProvider.System); Resources = new(Lifecycle);
        if (useHostTestTrust) Signer.ImportPkcs8PrivateKey(UpdateSourceSeed.TestSigningKey(), out _);
        Verifier = NewVerifier(Signer.ExportSubjectPublicKeyInfo(), stableFloor, developFloor);
        Store = NewStore(Verifier);
    }

    internal ExtensionCatalogFixture(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        RecoveryIndex index, ResourceLeaseManager resources, HostLifecycle lifecycle)
    {
        Instance = instance; Database = database; Lease = lease;
        Index = index; Resources = resources; Lifecycle = lifecycle;
        _ownsLease = false;
        Verifier = NewVerifier(Signer.ExportSubjectPublicKeyInfo());
        Store = NewStore(Verifier);
    }

    internal ExtensionCatalogVerifier NewVerifier(byte[] publicKey, long stableFloor = 1, long developFloor = 1) =>
        new(Contracts, new StoreTrustPolicy(new Dictionary<string, byte[]> { ["fixture-only"] = publicKey },
            MetadataRoot, [Artifacts], stableFloor, developFloor, new(0, 18, 0), "0.1.0"));

    internal ExtensionCatalogStore NewStore(ExtensionCatalogVerifier verifier) => new(Instance, Database, Lease, Resources, Contracts, verifier);
    internal VerifiedExtensionCatalog Verify(SignedCatalogBytes bytes, ExtensionStoreChannel channel = ExtensionStoreChannel.Stable) =>
        Verifier.Verify(bytes.Pointer, bytes.Catalog, bytes.Signature, channel);

    internal SignedCatalogBytes Signed(long sequence = 1, string channel = "stable", Action<JsonObject>? edit = null, Guid? generation = null)
    {
        var id = (generation ?? Guid.NewGuid()).ToString("D");
        var catalog = new JsonObject
        {
            ["format"] = "nexus.store.catalog", ["version"] = 1, ["generationId"] = id, ["sequence"] = sequence,
            ["channel"] = channel, ["publishedAt"] = "2026-10-22T00:00:00.0000000Z",
            ["entries"] = new JsonArray(new JsonObject
            {
                ["extensionId"] = "fixture-plugin", ["artifactName"] = "FixturePlugin", ["packageVersion"] = "0.5.0",
                ["kind"] = "managed-code", ["minHostVersion"] = "0.18.0", ["sdkVersion"] = "0.1.0",
                ["artifact"] = new JsonObject { ["url"] = new Uri(Artifacts, "v0.5.0/FixturePlugin.zip").AbsoluteUri,
                    ["sizeBytes"] = 1234, ["sha256"] = new string('a', 64) }, ["manifestSha256"] = new string('b', 64)
            })
        };
        edit?.Invoke(catalog);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(catalog);
        var signature = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "nexus.detached.signature", version = 1, keyId = "fixture-only",
            algorithm = "ecdsa-p256-sha256", encoding = "base64-p1363",
            signature = Convert.ToBase64String(Signer.SignData(bytes, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        });
        var prefix = new Uri(MetadataRoot, channel + "/generations/" + id + "/");
        var pointer = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "nexus.store.pointer", version = 1, generationId = id, sequence, channel,
            catalogUrl = new Uri(prefix, "catalog.json").AbsoluteUri,
            catalogSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            signatureUrl = new Uri(prefix, "catalog.sig.json").AbsoluteUri
        });
        return new(pointer, bytes, signature);
    }

    internal static byte[] Edit(byte[] bytes, string name, JsonNode? value)
    {
        var node = JsonNode.Parse(bytes)!.AsObject(); node[name] = value;
        return JsonSerializer.SerializeToUtf8Bytes(node);
    }

    internal long Count(string table)
    {
        if (table is not ("extension_catalog_heads" or "extension_catalog_acceptances" or "operations")) throw new ArgumentException();
        using var connection = DatabaseConnections.Open(Database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM " + table + ";";
        return (long)command.ExecuteScalar()!;
    }

    internal void Sql(string sql)
    {
        using var connection = DatabaseConnections.Open(Database.DatabasePath);
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    public void Dispose() { if (_ownsLease) Lease.Dispose(); Signer.Dispose(); }
}
