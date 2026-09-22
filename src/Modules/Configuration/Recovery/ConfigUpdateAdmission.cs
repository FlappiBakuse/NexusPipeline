using NexusPipeline.Modules.Configuration.Paths;

namespace NexusPipeline.Modules.Configuration.Recovery;

/// <summary>只读检查配置现场；更新不能把尚需当前恢复器处理的数据交给另一版本。</summary>
internal static class ConfigUpdateAdmission
{
    internal static bool HasPendingRecovery(string dataDirectory)
    {
        try
        {
            if (!ExistsOrThrow(dataDirectory)) return false;
            if (!IsOrdinaryDirectory(dataDirectory)) return true;
            foreach (string script in Directory.EnumerateDirectories(dataDirectory))
            {
                if (!IsOrdinaryDirectory(script) || OwnerHasResidue(script)) return true;
                foreach (string user in Directory.EnumerateDirectories(script))
                {
                    if (Path.GetFileName(user).Equals(ConfigPaths.WorkDirName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!IsOrdinaryDirectory(user) || OwnerHasResidue(user)) return true;
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsOrdinaryDirectory(string path) =>
        (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory;

    // Path.Exists also returns false for access/I/O failures. Only a definite missing
    // path may be treated as clean; all other failures reach the conservative outer catch.
    private static bool ExistsOrThrow(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static bool OwnerHasResidue(string owner)
    {
        if (ExistsOrThrow(Path.Combine(owner, ".session")) || ExistsOrThrow(Path.Combine(owner, ".session.bak"))) return true;
        string work = Path.Combine(owner, ConfigPaths.WorkDirName);
        if (!ExistsOrThrow(work)) return false;
        if (!IsOrdinaryDirectory(work)) return true;
        foreach (string entry in Directory.EnumerateFileSystemEntries(work))
        {
            if (!IsOrdinaryDirectory(entry)) return true;
            // Generated scripts are disposable; every other nonempty work area may contain recovery data,
            // including journal formats this version cannot parse. Never traverse reparse points.
            if (!Path.GetFileName(entry).Equals("script", StringComparison.OrdinalIgnoreCase)
                && Directory.EnumerateFileSystemEntries(entry).Any()) return true;
        }
        return false;
    }
}
