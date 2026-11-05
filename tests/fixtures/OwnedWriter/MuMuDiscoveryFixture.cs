using System.Diagnostics;
using System.Text.Json;

internal static class MuMuDiscoveryFixture
{
    internal static async Task<int> RunAsync(bool child)
    {
        var directory = AppContext.BaseDirectory;
        var marker = Path.Combine(directory, "mumu-discovery");
        if (child)
        {
            await PublishAsync(marker + ".child");
            await Task.Delay(Timeout.InfiniteTimeSpan); return 0;
        }
        using var input = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(directory, "mumu-discovery-fixture.json")));
        var mode = input.RootElement.GetProperty("mode").GetString();
        var port = input.RootElement.GetProperty("port").GetInt32();
        await PublishAsync(marker + ".started");
        if (mode == "timeout") { await Task.Delay(Timeout.InfiniteTimeSpan); return 0; }
        using var descendant = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!, WorkingDirectory = directory, UseShellExecute = false,
            ArgumentList = { "mumu-discovery-fixture-child" }, CreateNoWindow = true
        }) ?? throw new IOException("fixture_child_missing");
        var ready = Stopwatch.StartNew();
        while (!File.Exists(marker + ".child") && ready.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        if (!File.Exists(marker + ".child")) throw new IOException("fixture_child_not_ready");
        var value = mode switch
        {
            "duplicate" => JsonSerializer.Serialize(new Dictionary<string, object>
                { ["3"] = new { adb_port = port }, ["4"] = new { adb_port = port } }),
            "malformed" => "{not-json",
            "overflow" => new string('x', 2097153),
            _ => JsonSerializer.Serialize(new Dictionary<string, object> { ["3"] = new { adb_port = port } })
        };
        await Console.Out.WriteAsync(value); await Console.Out.FlushAsync();
        await Console.Error.WriteAsync("readonly-info\n"); await Console.Error.FlushAsync();
        return mode == "nonzero" ? 23 : 0;
    }
    private static async Task PublishAsync(string path)
    {
        await File.WriteAllTextAsync(path + ".prepared", Environment.ProcessId.ToString());
        File.Move(path + ".prepared", path, overwrite: true);
    }
}
