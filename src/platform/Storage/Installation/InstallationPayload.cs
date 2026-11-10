using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Maintenance;

namespace NexusPipeline.Platform.Storage.Installation;

[SupportedOSPlatform("windows")]
internal sealed class InstallationPayload : IDisposable
{
    private readonly TreeReadLease _held;
    internal string Root { get; }
    internal string ManifestSha256 { get; }
    internal ApplicationManifest Manifest { get; }
    internal PhysicalObjectIdentity RootIdentity { get; }
    internal FileTreeSnapshot Snapshot { get; }
    internal IReadOnlyList<PhysicalTreeObject> Objects { get; }

    private InstallationPayload(string root, string digest, ApplicationManifest manifest, PhysicalObjectIdentity identity,
        FileTreeSnapshot snapshot, IReadOnlyList<PhysicalTreeObject> objects, TreeReadLease held)
    { Root = root; ManifestSha256 = digest; Manifest = manifest; RootIdentity = identity; Snapshot = snapshot; Objects = objects; _held = held; }

    internal static InstallationPayload Open(string applicationRoot, string expectedManifestSha256, string profile, bool exclusiveRoot = false)
    {
        if (!InstallationIdentityCodec.Digest(expectedManifestSha256) || profile is not ("production" or "test"))
            throw InstallationIdentityCodec.Invalid();
        var root = PhysicalPathPolicy.NormalizeWithoutLinks(applicationRoot);
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(root, ApplicationTree.ManifestPath));
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > StrictJson.MaximumBytes) throw InstallationIdentityCodec.Invalid();
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != expectedManifestSha256) throw InstallationIdentityCodec.Invalid();
        var manifest = ApplicationManifest.Read(new ContractReader(), bytes, profile);
        var scanner = new FileTreeScanner(SnapshotBudget.Default);
        var snapshot = exclusiveRoot ? scanner.Capture(root, default) : ApplicationTree.Capture(scanner, root, default);
        var objects = PhysicalTreeIdentity.Capture(root, snapshot);
        PhysicalTreeIdentity.RequireSingleLinks(root, snapshot);
        var held = TreeReadLease.AcquireApplication(root, PhysicalObjectIdentity.Read(Path.GetDirectoryName(root)!), snapshot, objects, exclusiveRoot);
        try
        {
            ApplicationManifest.RequireSnapshot(snapshot, manifest, bytes.LongLength, expectedManifestSha256, allowMissing: false);
            using var host = File.OpenRead(Path.Combine(root, ApplicationTree.HostEntry));
            UpdateBuildProfile.RequireBundle(host, "NexusPipeline.Host.dll", profile);
            using var window = File.OpenRead(Path.Combine(root, ApplicationTree.MaintenanceEntry));
            var hostSha256 = manifest.Files.Single(file => file.RelativePath == ApplicationTree.HostEntry).Sha256;
            UpdateBuildProfile.RequireWindow(window, profile, hostSha256);
            if (manifest.Files.Any(file => file.RelativePath == ApplicationTree.LauncherEntry))
            {
                using var launcher = new FileStream(Path.Combine(root, ApplicationTree.LauncherEntry), FileMode.Open, FileAccess.Read, FileShare.Read);
                UpdateBuildProfile.RequireBundle(launcher, "NexusPipeline.Launcher.dll", profile);
            }
            return new(root, expectedManifestSha256, manifest, objects.Single(item => item.RelativePath.Length == 0).Identity,
                snapshot, Array.AsReadOnly(objects.ToArray()), held);
        }
        catch { held.Dispose(); throw; }
    }

    internal IDisposable Borrow() => _held.Borrow();

    public void Dispose() => _held.Dispose();
}
