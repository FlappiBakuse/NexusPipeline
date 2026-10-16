namespace NexusPipeline.Platform.Storage;

internal static class PayloadPathSafety
{
    internal static string NativePath(string path)
    {
        string full = Path.GetFullPath(path);
        // Raw Win32 calls do not receive the long-path normalization performed by System.IO.
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    internal static void RequireLinkFree(string path)
    {
        for (string? part = Path.GetFullPath(path); part is not null; part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("payload.link_path");
    }
}
