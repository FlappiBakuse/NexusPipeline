using System.Diagnostics;
using System.Text.Json;
using NexusPipeline.Platform.Processes;

internal static class MaaStartupFixture
{
    internal static async Task<int> RunAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "maa-startup-fixture.json");
        OwnedFixtureEvidence.RequirePath(path);
        using var input = JsonDocument.Parse(File.ReadAllBytes(path));
        var executable = input.RootElement.GetProperty("executable").GetString()!;
        var marker = input.RootElement.GetProperty("marker").GetString()!;
        OwnedFixtureEvidence.RequirePath(executable); OwnedFixtureEvidence.RequirePath(marker);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var value in new[] { "capture-window-fixture", marker, "retry-static-frame" }) start.ArgumentList.Add(value);
        using var child = Process.Start(start) ?? throw new IOException("fixture_child_missing");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!File.Exists(marker + "." + child.Id))
        {
            if (child.HasExited) throw new IOException("fixture_child_exited");
            await Task.Delay(10, deadline.Token);
        }
        File.AppendAllText(marker + ".pc-starts", JsonSerializer.Serialize(ProcessIdentityReader.Read(child.Id)) + "\n");
        File.AppendAllText(marker + ".agent", "login\n");
        return 0;
    }
}
