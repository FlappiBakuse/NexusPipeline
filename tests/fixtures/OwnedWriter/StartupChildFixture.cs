using System.Diagnostics;

internal static class StartupChildFixture
{
    internal static async Task<int> RunAsync(string executable, string marker)
    {
        OwnedFixtureEvidence.RequirePath(executable); OwnedFixtureEvidence.RequirePath(marker);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var value in new[] { "capture-window-fixture", marker, "static-frame" }) start.ArgumentList.Add(value);
        using var child = Process.Start(start) ?? throw new IOException("fixture_child_missing");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!File.Exists(marker))
        {
            if (child.HasExited) throw new IOException("fixture_child_exited");
            await Task.Delay(10, deadline.Token);
        }
        File.WriteAllText(marker + ".launcher-returned", child.Id.ToString());
        return 0;
    }
}
