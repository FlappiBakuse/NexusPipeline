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

    internal static InstallationPayload Open(string applicationRoot, string expectedManifestSha256, string profile)
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
        var snapshot = ApplicationTree.Capture(new(SnapshotBudget.Default), root, default);
        var objects = PhysicalTreeIdentity.Capture(root, snapshot);
        PhysicalTreeIdentity.RequireSingleLinks(root, snapshot);
        var held = TreeReadLease.AcquireApplication(root, PhysicalObjectIdentity.Read(Path.GetDirectoryName(root)!), snapshot, objects);
        try
        {
            ApplicationManifest.RequireSnapshot(snapshot, manifest, bytes.LongLength, expectedManifestSha256, allowMissing: false);
            foreach (var (entry, assembly) in new[] { (ApplicationTree.HostEntry, "NexusPipeline.Host.dll"),
                (ApplicationTree.MaintenanceEntry, "NexusPipeline.Maintenance.dll") })
            {
                using var program = new FileStream(Path.Combine(root, entry), FileMode.Open, FileAccess.Read, FileShare.Read);
                UpdateBuildProfile.RequireBundle(program, assembly, profile);
            }
            return new(root, expectedManifestSha256, manifest, objects.Single(item => item.RelativePath.Length == 0).Identity,
                snapshot, Array.AsReadOnly(objects.ToArray()), held);
        }
        catch { held.Dispose(); throw; }
    }

    internal IDisposable Borrow() => _held.Borrow();

    public void Dispose() => _held.Dispose();
}
