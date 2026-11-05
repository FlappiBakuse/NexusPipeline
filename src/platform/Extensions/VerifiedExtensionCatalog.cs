using System.Collections.Immutable;
using NexusPipeline.Core.Extensions;

namespace NexusPipeline.Platform.Extensions;

internal sealed class VerifiedExtensionCatalog
{
    private readonly byte[] _pointer, _catalog, _signature;
    internal ExtensionCatalogIdentity Identity { get; }
    internal string KeyId { get; }
    internal DateTimeOffset PublishedAt { get; }
    internal ImmutableArray<ExtensionCatalogEntry> Entries { get; }

    private VerifiedExtensionCatalog(ExtensionCatalogIdentity identity, string keyId, DateTimeOffset publishedAt,
        ImmutableArray<ExtensionCatalogEntry> entries, byte[] pointer, byte[] catalog, byte[] signature)
    {
        Identity = identity; KeyId = keyId; PublishedAt = publishedAt; Entries = entries;
        _pointer = pointer.ToArray(); _catalog = catalog.ToArray(); _signature = signature.ToArray();
    }

    internal static VerifiedExtensionCatalog Verify(ExtensionCatalogVerifier verifier, ReadOnlyMemory<byte> pointer,
        ReadOnlyMemory<byte> catalog, ReadOnlyMemory<byte> signature, ExtensionStoreChannel expectedChannel) =>
        verifier.VerifyCore<VerifiedExtensionCatalog>(pointer, catalog, signature, expectedChannel,
            (identity, key, date, entries, p, c, s) => new(identity, key, date, entries, p, c, s));

    internal byte[] PointerBytes() => _pointer.ToArray();
    internal byte[] CatalogBytes() => _catalog.ToArray();
    internal byte[] SignatureBytes() => _signature.ToArray();
}
