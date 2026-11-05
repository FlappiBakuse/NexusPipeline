using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Platform.Extensions;

internal sealed record ReadProviderPackage(string Root, string ArchivePath, ExtensionManifest Manifest,
    byte[] ManifestBytes, ImmutableArray<VerifiedPackageFile> Files, VerifiedPackageFile Archive);

[SupportedOSPlatform("windows")]
internal static class ProviderPackageReader
{
    internal const int MaximumFiles = ExtensionPackageArchive.MaximumFiles;
    internal const long MaximumPackageBytes = ExtensionPackageArchive.MaximumPackageBytes;

    internal static ReadProviderPackage Read(string requestedArchive, string requestedRoot,
        string extensionId, ContractReader contracts, List<IDisposable> held)
    {
        var archivePath = PhysicalPathPolicy.NormalizeWithoutLinks(requestedArchive);
        var root = PhysicalPathPolicy.NormalizeWithoutLinks(requestedRoot);
        if (Path.GetRelativePath(root, archivePath) is var relative &&
            relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            throw new IOException("execution_provider_archive_inside_package");
        held.Add(StableDirectoryLease.Acquire(root));
        held.Add(StableDirectoryLease.Acquire(Path.GetDirectoryName(archivePath)!));
        var archiveStream = OpenRead(archivePath, held);
        if (archiveStream.Length is < 1 or > MaximumPackageBytes) throw new IOException("execution_provider_package_size_invalid");
        var archive = Capture(archivePath, Path.GetFileName(archivePath), archiveStream);
        archiveStream.Position = 0;
        using var package = ExtensionPackageArchive.Open(archiveStream);
        var entries = package.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        var names = Enumerate(root, held);
        if (!entries.Keys.OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(names, StringComparer.Ordinal) ||
            !entries.ContainsKey("manifest.json")) throw new IOException("execution_provider_package_set_unbound");
        var files = ImmutableArray.CreateBuilder<VerifiedPackageFile>();
        byte[]? manifest = null;
        foreach (var name in names)
        {
            var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(root, name));
            var file = OpenRead(path, held);
            var captured = Capture(path, name, file);
            var entry = entries[name];
            if (captured.SizeBytes != entry.SizeBytes || captured.Sha256 != entry.Sha256)
                throw new IOException("execution_provider_deployed_bytes_unbound");
            files.Add(captured);
            if (name == "manifest.json")
            {
                if (file.Length is < 1 or > StrictJson.MaximumBytes) throw new IOException("execution_provider_manifest_size_invalid");
                file.Position = 0; manifest = new byte[checked((int)file.Length)]; file.ReadExactly(manifest); file.Position = 0;
            }
        }
        var bytes = manifest ?? throw new IOException("execution_provider_manifest_missing");
        var admitted = new ExtensionManifestAdmission(contracts).ReadCurrent(bytes, extensionId);
        return new(root, archivePath, admitted, bytes, files.ToImmutable(), archive);
    }

    internal static string[] Enumerate(string root, List<IDisposable> held)
    {
        var files = new List<string>();
        var pending = new Stack<string>(); pending.Push(root);
        var count = 0;
        while (pending.TryPop(out var directory))
        {
            if (++count > MaximumFiles) throw new IOException("execution_provider_directory_limit");
            held.Add(StableDirectoryLease.Acquire(directory));
            if (PhysicalPathPolicy.NormalizeWithoutLinks(directory) != directory) throw new IOException("execution_provider_path_invalid");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("filesystem_reparse_point_rejected");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else
                {
                    files.Add(Path.GetRelativePath(root, entry).Replace('\\', '/'));
                    if (files.Count > MaximumFiles) throw new IOException("execution_provider_package_file_limit");
                }
            }
        }
        return files.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    internal static FileStream OpenRead(string path, List<IDisposable> held)
    {
        using var parent = PhysicalDirectoryHandle.Open(Path.GetDirectoryName(path)!);
        var handle = parent.OpenFile(Path.GetFileName(path), write: false);
        FileStream input;
        try { input = new FileStream(handle, FileAccess.Read); }
        catch { handle.Dispose(); throw; }
        held.Add(input);
        PhysicalObjectIdentity.RequireSingleLink(path);
        if (PhysicalObjectIdentity.Read(input.SafeFileHandle) != PhysicalObjectIdentity.Read(path))
            throw new IOException("execution_provider_package_identity_changed");
        return input;
    }

    private static VerifiedPackageFile Capture(string path, string relative, FileStream stream) =>
        new(relative, stream.Length, Convert.ToHexStringLower(SHA256.HashData(stream)),
            PhysicalObjectIdentity.Read(stream.SafeFileHandle), PhysicalObjectIdentity.Read(Path.GetDirectoryName(path)!));

}
