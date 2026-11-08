internal static class PluginSliceHarness
{
    internal static async Task<int> RunAsync(string[] args, string operation)
    {
        if (!operation.EndsWith("-plugin", StringComparison.Ordinal) || operation.Any(value => !char.IsAsciiLetterLower(value) && value != '-')) throw new ArgumentException("Unknown plugin slice operation");
        if (args is not [var input, var report, var receipt]) return 2;
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new IOException("Missing fixture root"));
        var working = Path.GetDirectoryName(Path.GetFullPath(input))!;
        if (!working.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            Path.GetDirectoryName(Path.GetFullPath(report)) != working || Path.GetDirectoryName(Path.GetFullPath(receipt)) != working)
            throw new IOException("Media plugin harness paths leave the owned root");
        using var lease = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var settings = System.Text.Json.JsonDocument.Parse(lease);
        var instance = settings.RootElement.GetProperty("instanceId").GetGuid();
        var attempt = settings.RootElement.GetProperty("attemptId").GetGuid();
        if (instance == Guid.Empty || attempt == Guid.Empty) throw new IOException("Plugin slice execution identity is empty");
        return await OwnedFixtureProcess.RunAsync(Environment.ProcessPath!, [operation, input, report], working, report, receipt,
            TimeSpan.FromSeconds(90), instance, attempt);
    }
}
