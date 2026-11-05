using System.Security.Cryptography;
using System.Runtime.Versioning;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Security;

namespace NexusPipeline.Platform.Storage.Secrets;

internal sealed record SecretPayloadIdentity(Guid SecretId, string Scheme, string OwnerSid, string Digest, long SizeBytes);
internal sealed class SecretPayloadLease : IDisposable
{
    private readonly FileStream _held;
    private readonly string? _path;
    private readonly PhysicalObjectIdentity _object;
    private readonly PhysicalObjectIdentity? _parent;
    internal SecretPayloadIdentity Identity { get; }

    internal SecretPayloadLease(SecretPayloadIdentity identity, FileStream held, string? path = null)
    {
        Identity = identity; _held = held; _path = path;
        _object = PhysicalObjectIdentity.Read(held.SafeFileHandle);
        _parent = path is null ? null : PhysicalObjectIdentity.Read(Path.GetDirectoryName(path)!);
    }

    internal void RequireCurrent()
    {
        if (_path is null || PhysicalPathPolicy.NormalizeWithoutLinks(_path) != _path ||
            PhysicalObjectIdentity.Read(_path) != _object || _held.Length != Identity.SizeBytes ||
            PhysicalObjectIdentity.Read(Path.GetDirectoryName(_path)!) != _parent)
            throw new IOException("secret_payload_object_changed");
    }

    public void Dispose() => _held.Dispose();
}

[SupportedOSPlatform("windows")]
internal sealed class SecretPayloadFiles(RuntimeInstance instance, UserSecretProtector protector)
{
    internal SecretPayloadLease Create(SecretPayloadIdentity identity, ProtectedSecretPayload payload)
    {
        Verify(identity);
        var bytes = payload.Ciphertext.ToArray();
        if (bytes.Length != identity.SizeBytes || payload.ProtectionScheme != identity.Scheme || payload.OwnerSid != identity.OwnerSid
            || Convert.ToHexStringLower(SHA256.HashData(bytes)) != identity.Digest) throw new IOException("secret_payload_identity_changed");
        var path = PathFor(identity);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            output.Write(bytes);
            output.Flush(flushToDisk: true);
        }
        return new(identity, OpenVerified(identity));
    }

    internal SecretPayloadLease HoldVerified(SecretPayloadIdentity identity, bool trackPath = false)
        => new(identity, OpenVerified(identity), trackPath ? PathFor(identity) : null);

    internal SecretValue Read(SecretPayloadIdentity identity)
    {
        using var input = OpenVerified(identity);
        var bytes = new byte[checked((int)identity.SizeBytes)];
        input.ReadExactly(bytes);
        return protector.Unprotect(instance.InstanceId, identity.SecretId, new(identity.Scheme, identity.OwnerSid, identity.Digest, bytes));
    }

    private FileStream OpenVerified(SecretPayloadIdentity identity)
    {
        Verify(identity);
        var input = new FileStream(PathFor(identity), FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (input.Length != identity.SizeBytes || Convert.ToHexStringLower(SHA256.HashData(input)) != identity.Digest)
                throw new IOException("secret_payload_bytes_unverified");
            input.Position = 0;
            return input;
        }
        catch { input.Dispose(); throw; }
    }

    internal string PathFor(SecretPayloadIdentity identity) => PhysicalPathPolicy.NormalizeWithoutLinks(
        Path.Combine(instance.RuntimeRoot, "secrets", "payloads", $"{identity.SecretId:D}-{identity.Digest}.bin"));

    private static void Verify(SecretPayloadIdentity identity)
    {
        if (identity.SecretId == Guid.Empty || identity.SizeBytes is < 1 or > 1048576 || identity.Scheme != UserSecretProtector.Scheme
            || !identity.OwnerSid.StartsWith("S-1-", StringComparison.Ordinal) || identity.Digest.Length != 64
            || identity.Digest.Any(character => character is not (>= 'a' and <= 'f' or >= '0' and <= '9')))
            throw new IOException("secret_payload_metadata_unverified");
    }
}

