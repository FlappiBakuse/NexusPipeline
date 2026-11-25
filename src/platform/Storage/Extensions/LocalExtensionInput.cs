using System.Collections.Immutable;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed record LocalExtensionInput(string Path, string CandidateId, ExtensionManifest Manifest,
    ExtensionCatalogEntry Entry, ArchivedFileTree? Tree, string? ArchivePath, PhysicalObjectIdentity ObjectIdentity)
{
    private static readonly SnapshotBudget Budget = new(4096, ExtensionPackageArchive.MaximumPackageBytes);

    internal static LocalExtensionInput Read(string selected, ContractReader contracts,
        CancellationToken cancellation)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(selected);
        if (!Directory.Exists(path)) return ReadZip(path, contracts, cancellation);
        var parent = PhysicalObjectIdentity.Read(System.IO.Path.GetDirectoryName(path)!);
        using var held = TreeReadLease.Capture(path, parent, new(Budget), cancellation, "extension.local.hash", out var captured);
        if (captured.Snapshot.Shape != FileObjectKind.Directory) throw Invalid();
        var files = captured.Snapshot.Entries.Where(entry => entry.Kind == FileObjectKind.File).ToArray();
        var manifestEntry = files.SingleOrDefault(entry => entry.RelativePath == "manifest.json") ?? throw Invalid();
        if (manifestEntry.SizeBytes > 262144 || files.Length > ExtensionPackageArchive.MaximumFiles) throw Invalid();
        var bytes = File.ReadAllBytes(System.IO.Path.Combine(path, "manifest.json"));
        if (Digest(bytes) != manifestEntry.Sha256) throw Invalid();
        var manifest = ReadManifest(contracts, bytes);
        var entry = Describe(manifest, 0, "", manifestEntry.Sha256!);
        VerifiedExtensionPackage.RequireManifest(entry, manifest, bytes,
            files.Select(file => new ExtensionPackageFile(file.RelativePath, file.SizeBytes, file.Sha256!, 0)).ToImmutableArray());
        var tree = new ArchivedFileTree(captured.Snapshot, captured.Objects, files.Select(file =>
            new BlobReference($"blobs/content/{file.Sha256}.bin", file.SizeBytes, file.Sha256!)).Distinct().ToArray());
        var identity = Digest(JsonSerializer.SerializeToUtf8Bytes(new { path, captured.Snapshot, captured.Objects }));
        return new(path, identity, manifest, entry, tree, null, tree.OriginalIdentity!);
    }

    internal static LocalExtensionInput ReadZip(string path, ContractReader contracts, CancellationToken cancellation)
    {
        using var draft = OpenDraft(path);
        var input = draft.Input;
        if (input.Length is < 1 or > ExtensionPackageArchive.MaximumPackageBytes) throw Invalid();
        var digest = Convert.ToHexStringLower(SHA256.HashData(input));
        input.Position = 0;
        using var zip = ExtensionPackageArchive.Open(input, cancellation);
        var bytes = zip.ReadManifest(cancellation);
        var manifest = ReadManifest(contracts, bytes);
        var entry = Describe(manifest, input.Length, digest, Digest(bytes));
        VerifiedExtensionPackage.RequireManifest(entry, manifest, bytes, zip.Files);
        VerifiedExtensionPackage.RequireContents(manifest, zip, cancellation);
        var file = PhysicalObjectIdentity.Read(input.SafeFileHandle);
        var identity = Digest(JsonSerializer.SerializeToUtf8Bytes(new { path, digest, file }));
        return new(path, identity, manifest, entry, null, path, file);
    }

    internal LocalArchiveDraft? PrepareArchive(ExtensionCatalogEntry entry,
        CancellationToken cancellation)
    {
        if (ArchivePath is not { } archive) return null;
        cancellation.ThrowIfCancellationRequested();
        var draft = OpenDraft(archive);
        if (draft.Input.Length == entry.ArtifactBytes) return draft;
        draft.Dispose();
        throw Invalid();
    }

    internal LocalArchiveDraft MakeArchive(RuntimeInstance instance, CancellationToken cancellation)
    {
        var tree = Tree ?? throw Invalid();
        using var hold = TreeReadLease.Acquire(Path, PhysicalObjectIdentity.Read(System.IO.Path.GetDirectoryName(Path)!), tree);
        var staging = PhysicalPathPolicy.NormalizeWithoutLinks(System.IO.Path.Combine(instance.RuntimeRoot, "staging"));
        var name = System.IO.Path.Combine(staging, $"local-extension-{Guid.NewGuid():N}.zip");
        PhysicalObjectIdentity? owned = null;
        try
        {
            using (var output = new FileStream(name, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read))
            {
                owned = PhysicalObjectIdentity.Read(output.SafeFileHandle);
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                    foreach (var member in tree.Snapshot.Entries.Where(member => member.RelativePath.Length != 0))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var directory = member.Kind == FileObjectKind.Directory;
                        var entry = zip.CreateEntry(member.RelativePath + (directory ? "/" : ""), CompressionLevel.NoCompression);
                        entry.LastWriteTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                        if (directory) continue;
                        using var source = File.OpenRead(System.IO.Path.Combine(Path, member.RelativePath));
                        using var destination = entry.Open();
                        source.CopyTo(destination);
                    }
                output.Flush(flushToDisk: true);
            }
            var result = OpenDraft(name, owned);
            if (owned != PhysicalObjectIdentity.Read(result.Input.SafeFileHandle))
            { result.Dispose(); throw Invalid(); }
            return result;
        }
        catch { LocalArchiveDraft.RemoveOwned(name, owned); throw; }
    }

    private static ExtensionManifest ReadManifest(ContractReader contracts, byte[] bytes)
    {
        var manifest = contracts.Read<ExtensionManifest>(bytes, "extension-manifest.schema.json");
        ExtensionIdentifiers.Require(manifest.ExtensionId);
        return new ExtensionManifestAdmission(contracts).ReadCurrent(bytes, manifest.ExtensionId);
    }

    private static ExtensionCatalogEntry Describe(ExtensionManifest manifest, long bytes, string sha, string manifestSha) =>
        new(manifest.ExtensionId, manifest.ArtifactName, manifest.PackageVersion, manifest.Kind, manifest.MinHostVersion,
            manifest.SdkVersion, "", bytes, sha, manifestSha)
        { Metadata = ExtensionMetadataReader.Read(manifest.Metadata) };

    private static LocalArchiveDraft OpenDraft(string candidate, PhysicalObjectIdentity? owned = null)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(candidate);
        var held = new List<IDisposable>();
        try
        {
            held.Add(StableDirectoryLease.Acquire(System.IO.Path.GetDirectoryName(path)!));
            return new(path, ProviderPackageReader.OpenRead(path, held), held, owned);
        }
        catch
        {
            for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
            throw;
        }
    }

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static IOException Invalid() => new("extension_local_package_invalid");
}

[SupportedOSPlatform("windows")]
internal sealed class LocalArchiveDraft(string path, FileStream input, List<IDisposable> held, PhysicalObjectIdentity? owned) : IDisposable
{
    internal string Path { get; } = path;
    internal FileStream Input { get; } = input;

    public void Dispose()
    {
        for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
        RemoveOwned(Path, owned);
    }

    internal static void RemoveOwned(string path, PhysicalObjectIdentity? owned)
    {
        if (owned is null) return;
        try
        {
            if (File.Exists(path) && PhysicalObjectIdentity.Read(path) == owned)
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
