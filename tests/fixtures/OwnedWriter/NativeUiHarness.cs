using System.Text.Json;

internal static class NativeUiHarness
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var input, var report, var receipt]) return 2;
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new IOException("Missing fixture root"));
        var working = Path.GetDirectoryName(Path.GetFullPath(input))!;
        if (!working.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || Path.GetDirectoryName(Path.GetFullPath(report)) != working || Path.GetDirectoryName(Path.GetFullPath(receipt)) != working)
            throw new IOException("Native UI harness paths leave the owned root");
        using var document = JsonDocument.Parse(File.ReadAllBytes(input));
        var settings = document.RootElement;
        if (Path.GetFullPath(settings.GetProperty("report").GetString()!) != Path.GetFullPath(report)) throw new IOException("UI report identity differs");
        var release = Path.GetFullPath(settings.GetProperty("release").GetString()!);
        if (Path.GetDirectoryName(release) != working) throw new IOException("UI release escaped owned run");
        return await OwnedFixtureProcess.RunAsync(settings.GetProperty("electronExecutable").GetString()!,
            [settings.GetProperty("application").GetString()!, input], working, report, receipt, TimeSpan.FromSeconds(90),
            settings.GetProperty("instanceId").GetGuid(), settings.GetProperty("attemptId").GetGuid(), release);
    }
}
