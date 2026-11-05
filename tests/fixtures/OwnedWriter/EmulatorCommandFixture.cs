using System.Text.Json;

internal static class EmulatorCommandFixture
{
    internal static async Task<int> RunAsync()
    {
        var config = Path.Combine(AppContext.BaseDirectory, "emulator-command-fixture.json");
        var marker = Path.Combine(AppContext.BaseDirectory, "emulator-command-started.json");
        OwnedFixtureEvidence.RequirePath(config); OwnedFixtureEvidence.RequirePath(marker);
        using var input = JsonDocument.Parse(File.ReadAllBytes(config));
        var mode = input.RootElement.GetProperty("mode").GetString();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        OwnedFixtureEvidence.WriteReport(marker, new { processId = Environment.ProcessId, createdAt = process.StartTime.ToUniversalTime() });
        if (mode == "timeout") await Task.Delay(Timeout.InfiniteTimeSpan);
        if (mode == "exit") { await Console.Error.WriteLineAsync("controlled-cli-exit"); return 17; }
        if (mode != "normal") return 19;
        await Console.Out.WriteLineAsync("0,controlled fixture,0,0,1,123,456"); return 0;
    }
}
