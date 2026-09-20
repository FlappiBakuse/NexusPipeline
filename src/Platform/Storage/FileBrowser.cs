namespace NexusPipeline.Platform.Storage;

/// <summary>受限文件浏览的 Platform 原语：规范化路径、枚举目录并跳过 reparse/symlink 条目。</summary>
internal sealed class FileBrowser
{
    public FileBrowserResult Browse(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            IReadOnlyList<string> drives = DriveInfo.GetDrives()
                .Where(drive => drive.IsReady
                    || drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network)
                .Select(drive => drive.RootDirectory.FullName)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new FileBrowserResult("", null, drives, Array.Empty<string>());
        }

        string fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException(fullPath);
        }
        if (IsReparsePoint(fullPath))
        {
            throw new UnauthorizedAccessException("reparse point is not browsable");
        }

        IReadOnlyList<string> directories = Directory.EnumerateDirectories(fullPath)
            .Where(directory => !IsReparsePoint(directory))
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        IReadOnlyList<string> files = Directory.EnumerateFiles(fullPath)
            .Where(file => !IsReparsePoint(file))
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string? parent = Directory.GetParent(fullPath)?.FullName;
        return new FileBrowserResult(fullPath, parent, directories, files);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return true;
        }
    }
}

internal sealed record FileBrowserResult(
    string Path,
    string? Parent,
    IReadOnlyList<string> Directories,
    IReadOnlyList<string> Files);
