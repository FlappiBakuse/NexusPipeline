namespace NexusPipeline.Platform.Execution.Judgement;

public static class JudgementRuntimeDiscovery
{
    public static string? FindPython()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var path = directory.Trim('"');
            if (!Path.IsPathFullyQualified(path)) continue;
            var candidate = Path.Combine(path, "python.exe");
            if (!File.Exists(candidate)) continue;
            try { return Storage.PhysicalPathPolicy.NormalizeWithoutLinks(candidate); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return null;
    }
}
