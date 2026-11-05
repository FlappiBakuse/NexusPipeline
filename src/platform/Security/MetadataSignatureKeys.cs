using System.Collections.Immutable;
using System.Security.Cryptography;

namespace NexusPipeline.Platform.Security;

internal sealed class MetadataSignatureKeys
{
    private readonly ImmutableDictionary<string, ImmutableArray<byte>> _keys;
    private readonly Func<IOException> _rejection;

    internal MetadataSignatureKeys(IReadOnlyDictionary<string, byte[]> publicKeys, Func<IOException> rejection)
    {
        _rejection = rejection;
        if (publicKeys.Count is < 1 or > 8) throw rejection();
        var keys = ImmutableDictionary.CreateBuilder<string, ImmutableArray<byte>>(StringComparer.Ordinal);
        foreach (var (id, bytes) in publicKeys)
        {
            if (id is not { Length: >= 2 and <= 80 } || id[0] is not (>= 'a' and <= 'z') ||
                id.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')) ||
                bytes.Length is < 1 or > 1024) throw rejection();
            var owned = bytes.ToArray();
            using var key = ECDsa.Create(); key.ImportSubjectPublicKeyInfo(owned, out var read);
            if (read != owned.Length || key.KeySize != 256 ||
                key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7") throw rejection();
            keys.Add(id, owned.ToImmutableArray());
        }
        _keys = keys.ToImmutable();
    }

    internal void Verify(string keyId, ReadOnlySpan<byte> original, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != 64 || !_keys.TryGetValue(keyId, out var spki)) throw _rejection();
        using var key = ECDsa.Create(); key.ImportSubjectPublicKeyInfo(spki.AsSpan(), out var read);
        if (read != spki.Length || !key.VerifyData(original, signature, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) throw _rejection();
    }
}
