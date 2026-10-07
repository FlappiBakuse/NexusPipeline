using System.ComponentModel;
using System.Runtime.InteropServices;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Modules.Updates;

internal sealed class UpdateCreatedArtifacts(string root)
{
    private readonly string _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
    private readonly Dictionary<string, (long Size, string Hash)> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

    private string Resolve(string path)
    {
        string full = Path.GetFullPath(path);
        if (!full.Equals(_root, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("update.creation_path");
        PayloadPathSafety.RequireLinkFree(full);
        return full;
    }

    internal void EnsureDirectory(string path)
    {
        string full = Resolve(path);
        if (Directory.Exists(full)) return;
        string parent = Path.GetDirectoryName(full)!;
        if (parent.Equals(_root, StringComparison.OrdinalIgnoreCase)
            || parent.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) EnsureDirectory(parent);
        else { PayloadPathSafety.RequireLinkFree(parent); Directory.CreateDirectory(parent); }
        if (!CreateDirectoryW(full, IntPtr.Zero))
            throw new IOException("update.created_directory_conflict", new Win32Exception(Marshal.GetLastWin32Error()));
        _directories.Add(full);
    }

    internal void FileCreated(string path, long size, string hash)
    {
        string full = Resolve(path);
        if (size < 0 || !UpdateInventory.Hash(hash) || !_files.TryAdd(full, (size, hash)))
            throw new InvalidDataException("update.creation_receipt");
    }

    internal UpdateInventory Inventory(string directory)
    {
        string full = Resolve(directory), prefix = full + Path.DirectorySeparatorChar;
        var entries = _files.Where(item => item.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(item => new UpdateOwnedEntry(Path.GetRelativePath(full, item.Key).Replace('\\', '/'), false, item.Value.Size, item.Value.Hash))
            .Concat(_directories.Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(path => new UpdateOwnedEntry(Path.GetRelativePath(full, path).Replace('\\', '/'), true, 0, null)))
            .OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
        var inventory = new UpdateInventory(entries); inventory.Validate(); inventory.RequireOwned(full);
        return inventory;
    }

    internal string FileHash(string path) => _files.TryGetValue(Resolve(path), out var entry)
        ? entry.Hash : throw new InvalidDataException("update.creation_receipt_missing");

    internal void Cleanup()
    {
        foreach (var item in _files)
        {
            string path = Resolve(item.Key);
            if (Directory.Exists(path) || File.Exists(path) && (new FileInfo(path).Length != item.Value.Size
                || UpdateApply.ImageHash(path) != item.Value.Hash)) throw new IOException("update.created_file_changed");
        }
        foreach (string directory in _directories)
        {
            Resolve(directory);
            if (File.Exists(directory) || Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory)
                .Any(path => !_directories.Contains(path) && !_files.ContainsKey(path)))
                throw new IOException("update.created_directory_unowned");
        }
        foreach (var item in _files)
            if (File.Exists(Resolve(item.Key))) VerifiedFileDeletion.Delete(item.Key, item.Value.Size, item.Value.Hash);
        foreach (string directory in _directories.OrderByDescending(path => path.Length))
            if (Directory.Exists(Resolve(directory))) Directory.Delete(directory, false);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr security);
}
