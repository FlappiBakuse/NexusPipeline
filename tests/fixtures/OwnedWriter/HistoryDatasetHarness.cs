internal static class HistoryDatasetHarness
{
    internal static async Task<int> RunAsync(string[] args, string operation = "history-dataset")
    {
        if (args is not [var root, var report, var receipt]) return 2;
        var allowed = Path.GetFullPath(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new IOException("Fixture root required"));
        if (new[] { root, report, receipt }.Any(path => !Path.GetFullPath(path).StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return 2;
        var executable = Environment.ProcessPath ?? throw new IOException("Fixture executable required");
        if (operation is not ("history-dataset" or "history-query" or "history-ui" or "history-ui-verify")) return 2;
        return await OwnedFixtureProcess.RunAsync(executable, [operation, root, report], Path.GetDirectoryName(root)!, report, receipt, TimeSpan.FromMinutes(10));
    }
}
