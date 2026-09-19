namespace NexusPipeline.Platform.Processes;

internal static class ExecutablePathRules
{
    public static readonly string[] ExecutableExtensions = { ".exe", ".bat", ".cmd", ".com", ".ps1" };

    public static bool IsExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ExecutableExtensions.Contains(ext);
    }
}
