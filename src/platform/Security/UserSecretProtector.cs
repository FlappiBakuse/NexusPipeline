using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using NexusPipeline.Core.Secrets;

namespace NexusPipeline.Platform.Security;

public sealed record ProtectedSecretPayload(string ProtectionScheme, string OwnerSid, string Sha256, ReadOnlyMemory<byte> Ciphertext);

[SupportedOSPlatform("windows")]
public sealed class UserSecretProtector
{
    public const string Scheme = "windows-user-dpapi-utf8-v1";

    public ProtectedSecretPayload Protect(Guid instanceId, Guid secretId, SecretValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var entropy = Entropy(instanceId, secretId);
        var plaintext = SecretPayloadCodec.Encode(instanceId, secretId, value);
        try
        {
            var ciphertext = WindowsDataProtection.Transform(plaintext, entropy, protect: true);
            using var identity = WindowsIdentity.GetCurrent();
            return new(Scheme, identity.User?.Value ?? throw new IOException("secret_owner_unavailable"),
                Convert.ToHexStringLower(SHA256.HashData(ciphertext)), ciphertext);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public SecretValue Unprotect(Guid instanceId, Guid secretId, ProtectedSecretPayload payload)
    {
        var entropy = Entropy(instanceId, secretId);
        using var identity = WindowsIdentity.GetCurrent();
        if (payload.ProtectionScheme != Scheme || payload.OwnerSid != identity.User?.Value || payload.Ciphertext.Length is < 1 or > 1048576 ||
            Convert.ToHexStringLower(SHA256.HashData(payload.Ciphertext.Span)) != payload.Sha256)
            throw new IOException("secret_payload_unverified");
        var ciphertext = payload.Ciphertext.ToArray();
        var plaintext = WindowsDataProtection.Transform(ciphertext, entropy, protect: false);
        try { return SecretPayloadCodec.Decode(instanceId, secretId, plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static byte[] Entropy(Guid instanceId, Guid secretId)
    {
        if (instanceId == Guid.Empty || secretId == Guid.Empty) throw new ArgumentException("secret_protection_identity_invalid");
        return Encoding.UTF8.GetBytes($"nexus.secret.protection/1/{instanceId:D}/{secretId:D}");
    }
}
