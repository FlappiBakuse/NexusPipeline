using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Installation;

internal sealed record InstallerFile(long SizeBytes, string Sha256, PhysicalObjectIdentity Identity);

internal sealed record InstallationIdentity(string Format, int Version, string Generation, Guid InstallationId,
    string UserSid, string ApplicationRoot, PhysicalObjectIdentity RootIdentity, Guid RuntimeId,
    PhysicalObjectIdentity RuntimeIdentity, string ManifestSha256, string ProductVersion, string State, DateTimeOffset CreatedAt,
    InstallerFile Worker, InstallerFile NativeEngine);

[SupportedOSPlatform("windows")]
internal static class InstallationIdentityCodec
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("nexus.installation/g0180/1");
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    internal static string UserSid
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? throw Invalid();
        }
    }

    internal static byte[] Seal(InstallationIdentity identity)
    {
        Validate(identity);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(identity, Options);
        try { return WindowsDataProtection.Transform(bytes, Entropy, protect: true); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static InstallationIdentity Unseal(byte[] sealedBytes, string applicationRoot)
    {
        if (sealedBytes.Length is < 1 or > 65536) throw Invalid();
        var bytes = WindowsDataProtection.Transform(sealedBytes, Entropy, protect: false);
        try
        {
            using var document = StrictJson.Parse(bytes, 32768, 8192);
            var identity = document.Deserialize<InstallationIdentity>(Options) ?? throw Invalid();
            Validate(identity);
            if (!StringComparer.OrdinalIgnoreCase.Equals(identity.ApplicationRoot,
                PhysicalPathPolicy.NormalizeWithoutLinks(applicationRoot))) throw Invalid();
            return identity;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static void Validate(InstallationIdentity identity)
    {
        if (identity.Format != "nexus.installation" || identity.Version != 1 || identity.Generation != "g0180" ||
            identity.InstallationId == Guid.Empty || identity.RuntimeId == Guid.Empty || identity.UserSid != UserSid ||
            identity.State is not ("active" or "retained-data") || identity.CreatedAt == default || identity.CreatedAt.Offset != TimeSpan.Zero ||
            identity.ApplicationRoot is null || identity.ApplicationRoot != PhysicalPathPolicy.NormalizeWithoutLinks(identity.ApplicationRoot) ||
            identity.ApplicationRoot.StartsWith("\\\\", StringComparison.Ordinal) || Path.GetDirectoryName(identity.ApplicationRoot) is null ||
            !System.Version.TryParse(identity.ProductVersion, out var product) || product.Major != 0 || product.Minor != 18 ||
            product.Build < 0 || product.Revision != -1 || product.ToString(3) != identity.ProductVersion ||
            !Digest(identity.ManifestSha256) || !Physical(identity.RootIdentity) || !Physical(identity.RuntimeIdentity) ||
            identity.Worker is null || identity.Worker.SizeBytes is < 1 or > 268435456 ||
            !Digest(identity.Worker.Sha256) || !Physical(identity.Worker.Identity) || identity.NativeEngine is null ||
            identity.NativeEngine.SizeBytes is < 1 or > 268435456 || !Digest(identity.NativeEngine.Sha256) ||
            !Physical(identity.NativeEngine.Identity)) throw Invalid();
    }

    internal static bool Physical(PhysicalObjectIdentity? value) => value is not null &&
        Hex(value.VolumeSerial, 16) && Hex(value.FileId, 32);

    internal static bool Digest(string? value) => Hex(value, 64);

    private static bool Hex(string? value, int length) => value?.Length == length &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static IOException Invalid() => new("installer_ownership_unverified");
}
