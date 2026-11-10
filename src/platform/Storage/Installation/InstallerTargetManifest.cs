using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Maintenance;

namespace NexusPipeline.Platform.Storage.Installation;

internal sealed class InstallerTargetManifest
{
    internal string Profile { get; }
    internal string Sha256 { get; }
    internal ReadOnlyMemory<byte> Bytes { get; }
    internal string ProductVersion => Manifest.HostVersion;
    internal long SizeBytes => Manifest.Files.Sum(file => file.SizeBytes) + Bytes.Length;
    private ApplicationManifest Manifest { get; }

    private InstallerTargetManifest(string profile, string digest, byte[] bytes, ApplicationManifest manifest)
    { Profile = profile; Sha256 = digest; Bytes = bytes; Manifest = manifest; }

    internal static InstallerTargetManifest Read(ReadOnlyMemory<byte> original, string expectedSha256, string profile)
    {
        if (!InstallationIdentityCodec.Digest(expectedSha256) || profile is not ("test" or "production") ||
            original.Length is < 1 or > StrictJson.MaximumBytes ||
            Convert.ToHexStringLower(SHA256.HashData(original.Span)) != expectedSha256)
            throw InstallationIdentityCodec.Invalid();
        var bytes = original.ToArray();
        var manifest = ApplicationManifest.Read(new ContractReader(), bytes, profile);
        return new(profile, expectedSha256, bytes, manifest);
    }

    internal static InstallerTargetManifest Read(InstallerCheckpoint checkpoint)
        => Read(checkpoint.TargetManifest ?? throw new IOException("installer_target_manifest_required"),
            checkpoint.ManifestSha256, checkpoint.Profile);

    internal void RequireInstalled(FileTreeSnapshot snapshot)
        => ApplicationManifest.RequireSnapshot(snapshot, Manifest, Bytes.Length, Sha256, allowMissing: false);
}
