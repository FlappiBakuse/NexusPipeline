using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace NexusPipeline.Platform.Storage.Installation;

internal sealed record InstallerRegistryValue(string Name, RegistryValueKind Kind, string? Text,
    string[]? Lines, string? Binary, long? Number);
internal sealed record InstallerRegistrySnapshot(bool Exists, InstallerRegistryValue[] Values);
internal sealed record InstallerMetadataFile(string Role, string Name, PhysicalObjectIdentity ParentIdentity,
    PhysicalObjectIdentity Identity, long SizeBytes, string Sha256);
internal sealed record InstallerMetadataDirectory(string Role, PhysicalObjectIdentity? Identity);
internal sealed record InstallerMetadataSnapshot(string Root, PhysicalObjectIdentity? RootIdentity,
    InstallerRegistrySnapshot Uninstall, InstallerRegistrySnapshot Ownership, InstallerMetadataFile[] Files,
    InstallerMetadataDirectory[] Directories);

[SupportedOSPlatform("windows")]
internal static class InstallerMetadataInventory
{
    internal static InstallerMetadataSnapshot Capture(string applicationRoot, InstallerPaths paths, string scope = "installation")
        => CaptureCore(applicationRoot, paths, scope, null);

    internal static InstallerMetadataSnapshot CaptureFrozen(InstallerMetadataSnapshot expected, InstallerPaths paths, string scope = "installation")
    {
        InstallerSnapshotCodec.Validate(expected);
        return CaptureCore(expected.Root, paths, scope, expected.Files);
    }

    private static InstallerMetadataSnapshot CaptureCore(string applicationRoot, InstallerPaths paths, string scope,
        InstallerMetadataFile[]? verified)
    {
        if (scope is not ("installation" or "ownership")) throw InstallationIdentityCodec.Invalid();
        var root = PhysicalPathPolicy.NormalizeWithoutLinks(applicationRoot);
        if (File.Exists(root)) throw InstallationIdentityCodec.Invalid();
        using var rootLease = Directory.Exists(root) ? StableDirectoryLease.Acquire(root) : null;
        var directories = new List<InstallerMetadataDirectory>();
        var directoryLeases = new List<StableDirectoryLease>();
        try
        {
            foreach (var role in new[] { "desktop", "group", "manager", "uninstall" })
            {
                var directory = DirectoryPath(root, paths, role);
                if (File.Exists(directory)) throw InstallationIdentityCodec.Invalid();
                if (Directory.Exists(directory)) directoryLeases.Add(StableDirectoryLease.Acquire(directory));
                directories.Add(new(role, Directory.Exists(directory) ? PhysicalObjectIdentity.Read(directory) : null));
            }
            return CaptureInside(root, paths, directories.ToArray(), scope, verified);
        }
        finally { for (var index = directoryLeases.Count - 1; index >= 0; index--) directoryLeases[index].Dispose(); }
    }

    private static InstallerMetadataSnapshot CaptureInside(string root, InstallerPaths paths, InstallerMetadataDirectory[] directories,
        string scope, InstallerMetadataFile[]? verified)
    {
        var files = new List<InstallerMetadataFile>();
        var uninstall = Path.Combine(root, "uninstall");
        PhysicalPathPolicy.NormalizeWithoutLinks(uninstall);
        if (Directory.Exists(uninstall))
            foreach (var entry in Directory.EnumerateFileSystemEntries(uninstall))
            {
                if (Path.GetFileName(entry) is not ("unins000.exe" or "unins000.dat" or "unins000.msg") || !File.Exists(entry))
                    throw InstallationIdentityCodec.Invalid();
                if (scope == "installation") files.Add(CaptureFile("uninstall", entry));
                else
                {
                    PhysicalPathPolicy.NormalizeWithoutLinks(entry);
                    PhysicalObjectIdentity.RequireSingleLink(entry);
                }
            }
        foreach (var name in new[] { InstallationAnchor.IdentityFile, InstallationAnchor.WorkerFile })
            AddIfPresent("manager", Path.Combine(paths.ManagerRoot, name));
        AddIfPresent("entry", Path.Combine(root, InstallationAnchor.PublicUninstaller));
        foreach (var (role, shortcut) in new[]
        {
            ("desktop", Path.Combine(paths.DesktopRoot, "NexusPipeline v0.18.0.lnk")),
            ("group", Path.Combine(paths.GroupRoot, "NexusPipeline v0.18.0.lnk")),
            ("group", Path.Combine(paths.GroupRoot, InstallationAnchor.UninstallShortcut))
        })
        {
            PhysicalPathPolicy.NormalizeWithoutLinks(shortcut);
            if (!File.Exists(shortcut)) continue;
            var held = CaptureFile(role, shortcut);
            if (verified is null) InstallerShortcut.Require(shortcut, root);
            else if (!verified.Contains(held)) throw InstallationIdentityCodec.Invalid();
            if (CaptureFile(role, shortcut) != held) throw InstallationIdentityCodec.Invalid();
            files.Add(held);
        }
        var result = new InstallerMetadataSnapshot(root, Directory.Exists(root) ? PhysicalObjectIdentity.Read(root) : null,
            CaptureRegistry(paths.UninstallRegistryKey), CaptureRegistry(paths.RegistryKey),
            files.OrderBy(file => file.Role, StringComparer.Ordinal).ThenBy(file => file.Name, StringComparer.Ordinal).ToArray(), directories);
        InstallerSnapshotCodec.Validate(result);
        return result;

        void AddIfPresent(string role, string path)
        {
            PhysicalPathPolicy.NormalizeWithoutLinks(path);
            if (Directory.Exists(path)) throw InstallationIdentityCodec.Invalid();
            if (File.Exists(path)) files.Add(CaptureFile(role, path));
        }
    }

    internal static InstallerRegistrySnapshot CaptureRegistry(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
        if (key is null) return new(false, []);
        var names = key.GetValueNames().Order(StringComparer.Ordinal).ToArray();
        if (key.GetSubKeyNames().Length != 0 || names.Length > 64 || names.Any(name => name.Length is < 1 or > 128 || name.Any(char.IsControl)))
            throw InstallationIdentityCodec.Invalid();
        var values = new List<InstallerRegistryValue>();
        foreach (var name in names)
        {
            var kind = key.GetValueKind(name);
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            values.Add((kind, value) switch
            {
                (RegistryValueKind.String or RegistryValueKind.ExpandString, string text) when text.Length <= 32768 => new(name, kind, text, null, null, null),
                (RegistryValueKind.MultiString, string[] lines) when lines.Length <= 64 && lines.All(line => line.Length <= 8192) => new(name, kind, null, lines, null, null),
                (RegistryValueKind.Binary, byte[] bytes) when bytes.Length <= 65536 => new(name, kind, null, null, Convert.ToBase64String(bytes), null),
                (RegistryValueKind.DWord, int number) => new(name, kind, null, null, null, number),
                (RegistryValueKind.QWord, long number) => new(name, kind, null, null, null, number),
                _ => throw InstallationIdentityCodec.Invalid()
            });
        }
        var result = new InstallerRegistrySnapshot(true, values.ToArray());
        if (JsonSerializer.SerializeToUtf8Bytes(result).Length > 262144) throw InstallationIdentityCodec.Invalid();
        return result;
    }

    internal static InstallerMetadataFile CaptureFile(string role, string requestedPath)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(requestedPath);
        var parent = Path.GetDirectoryName(path) ?? throw InstallationIdentityCodec.Invalid();
        using var directory = StableDirectoryLease.Acquire(parent);
        PhysicalObjectIdentity.RequireSingleLink(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > 268435456) throw InstallationIdentityCodec.Invalid();
        return new(role, Path.GetFileName(path), PhysicalObjectIdentity.Read(parent), PhysicalObjectIdentity.Read(input.SafeFileHandle),
            input.Length, Convert.ToHexStringLower(SHA256.HashData(input)));
    }

    internal static bool Equal(InstallerMetadataSnapshot left, InstallerMetadataSnapshot right)
        => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

    internal static string DirectoryPath(string root, InstallerPaths paths, string role)
        => PhysicalPathPolicy.NormalizeWithoutLinks(role switch
        {
            "uninstall" => Path.Combine(root, "uninstall"), "manager" => paths.ManagerRoot,
            "desktop" => paths.DesktopRoot, "group" => paths.GroupRoot, "entry" => root, _ => throw InstallationIdentityCodec.Invalid()
        });

    internal static bool KnownFile(string role, string name) => role switch
    {
        "uninstall" => name is "unins000.exe" or "unins000.dat" or "unins000.msg",
        "manager" => name is InstallationAnchor.IdentityFile or InstallationAnchor.WorkerFile,
        "desktop" => name == "NexusPipeline v0.18.0.lnk",
        "group" => name is "NexusPipeline v0.18.0.lnk" or InstallationAnchor.UninstallShortcut,
        "entry" => name == InstallationAnchor.PublicUninstaller,
        _ => false
    };

}
