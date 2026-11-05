internal static class NativeEngineHarness
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var python, var worker, var input, var report, var receipt]) return 2;
        var allowed = Path.GetFullPath(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new IOException("Fixture root required"));
        if (!Path.IsPathFullyQualified(python) || !string.Equals(Path.GetFileName(python), "python.exe", StringComparison.OrdinalIgnoreCase)) return 2;
        if (new[] { worker, input, report, receipt }.Any(path => !Path.GetFullPath(path).StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return 2;
        return await OwnedFixtureProcess.RunAsync(python, ["-B", worker, input], Path.GetDirectoryName(input)!, report, receipt, TimeSpan.FromSeconds(90));
    }
}
