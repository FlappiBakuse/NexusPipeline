namespace NexusPipeline.Platform.Storage;

internal static class PayloadPathSafety
{
    internal static void RequireLinkFree(string path)
    {
        for (string? part = Path.GetFullPath(path); part is not null; part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("payload.link_path");
    }
}
