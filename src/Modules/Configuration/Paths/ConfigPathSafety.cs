namespace NexusPipeline.Modules.Configuration.Paths;

/// <summary>配置交换与恢复共用的相对路径边界检查。</summary>
internal static class ConfigPathSafety
{
    internal static string? ResolveWithin(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }
        try
        {
            string fullRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
            return candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
                ? candidate
                : null;
        }
        catch
        {
            return null;
        }
    }
}
