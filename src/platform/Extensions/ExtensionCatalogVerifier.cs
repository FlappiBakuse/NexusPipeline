using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Extensions;

internal sealed class ExtensionCatalogVerifier(ContractReader contracts, StoreTrustPolicy trust)
{
    internal const string Format = "nexus.store.catalog";
    internal VerifiedExtensionCatalog Verify(ReadOnlyMemory<byte> pointer, ReadOnlyMemory<byte> catalog,
        ReadOnlyMemory<byte> signature, ExtensionStoreChannel expectedChannel)
    {
        try { return VerifiedExtensionCatalog.Verify(this, pointer, catalog, signature, expectedChannel); }
        catch (Exception error) when (error is JsonException or CryptographicException or FormatException)
        { throw new IOException("extension_catalog_signature_invalid", error); }
    }

    internal T VerifyCore<T>(ReadOnlyMemory<byte> pointer, ReadOnlyMemory<byte> catalog, ReadOnlyMemory<byte> signature,
        ExtensionStoreChannel expectedChannel, Func<ExtensionCatalogIdentity, string, DateTimeOffset,
            ImmutableArray<ExtensionCatalogEntry>, byte[], byte[], byte[], T> accept)
        => DecodeCore(pointer, catalog, signature, expectedChannel, trust.Floor(expectedChannel), accept);

    internal void RequireRecorded(ReadOnlyMemory<byte> pointer, ReadOnlyMemory<byte> catalog,
        ReadOnlyMemory<byte> signature, ExtensionCatalogIdentity expected, string keyId, ExtensionCatalogEntry? entry)
    {
        try
        {
            var valid = DecodeCore(pointer, catalog, signature, expected.Channel, 1,
                (identity, actualKey, _, entries, _, _, _) =>
                    identity == expected && actualKey == keyId && (entry is null || entries.Contains(entry)));
            if (!valid) throw Invalid();
        }
        catch (Exception error) when (error is JsonException or CryptographicException or FormatException)
        { throw new IOException("extension_catalog_signature_invalid", error); }
    }

    private T DecodeCore<T>(ReadOnlyMemory<byte> pointer, ReadOnlyMemory<byte> catalog, ReadOnlyMemory<byte> signature,
        ExtensionStoreChannel expectedChannel, long minimumSequence, Func<ExtensionCatalogIdentity, string, DateTimeOffset,
            ImmutableArray<ExtensionCatalogEntry>, byte[], byte[], byte[], T> accept)
    {
        var p = Own(pointer); var c = Own(catalog); var s = Own(signature);
        var hint = contracts.Read<StorePointer>(p, "store-pointer.schema.json");
        var signed = contracts.Read<DetachedSignature>(s, "detached-signature.schema.json");
        if (signed.Algorithm != "ecdsa-p256-sha256" || signed.Encoding != "base64-p1363") throw Invalid();
        byte[] detached;
        try { detached = Convert.FromBase64String(signed.Signature); }
        catch (FormatException error) { throw new IOException("extension_catalog_signature_invalid", error); }
        if (detached.Length != 64 || Convert.ToBase64String(detached) != signed.Signature) throw Invalid();
        var sha = Convert.ToHexStringLower(SHA256.HashData(c));
        if (sha != hint.CatalogSha256) throw Invalid();
        trust.Verify(signed.KeyId, c, detached);
        var saved = contracts.Read<StoreCatalog>(c, "store-catalog.schema.json");
        var channel = saved.Channel switch
        { "stable" => ExtensionStoreChannel.Stable, "develop" => ExtensionStoreChannel.Develop, _ => throw Invalid() };
        if (channel != expectedChannel || hint.Channel != saved.Channel || hint.GenerationId != saved.GenerationId ||
            hint.Sequence != saved.Sequence || !Guid.TryParseExact(saved.GenerationId, "D", out var generation) || generation == Guid.Empty)
            throw Invalid();
        trust.RequireCatalogUrls(channel, generation, hint.CatalogUrl, hint.SignatureUrl);
        var identity = new ExtensionCatalogIdentity(channel, generation, saved.Sequence, sha);
        CatalogSequencePolicy.Require(identity, minimumSequence, null);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var artifacts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = ImmutableArray.CreateBuilder<ExtensionCatalogEntry>(saved.Entries.Count);
        foreach (var item in saved.Entries)
        {
            if (!ids.Add(item.ExtensionId) || !artifacts.Add(item.ArtifactName) || item.SdkVersion != trust.SdkVersion ||
                !Version.TryParse(item.MinHostVersion.Split('-')[0], out var minimum) || minimum > trust.HostVersion ||
                item.Artifact.SizeBytes > 268435456) throw Invalid();
            trust.RequireArtifactUrl(item.Artifact.Url);
            entries.Add(new(item.ExtensionId, item.ArtifactName, item.PackageVersion, item.Kind, item.MinHostVersion,
                item.SdkVersion, item.Artifact.Url, item.Artifact.SizeBytes, item.Artifact.Sha256, item.ManifestSha256)
                { Metadata = ExtensionMetadataReader.Read(item.Metadata) });
        }
        var published = DateTimeOffset.Parse(saved.PublishedAt, CultureInfo.InvariantCulture, DateTimeStyles.None);
        if (published.Offset != TimeSpan.Zero) throw Invalid();
        return accept(identity, signed.KeyId, published, entries.ToImmutable(), p, c, s);
    }

    internal long Floor(ExtensionStoreChannel channel) => trust.Floor(channel);
    private static byte[] Own(ReadOnlyMemory<byte> value)
    {
        if (value.Length is < 1 or > StrictJson.MaximumBytes ||
            value.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) throw Invalid();
        return value.ToArray();
    }
    private static IOException Invalid() => new("extension_catalog_signature_invalid");
}
