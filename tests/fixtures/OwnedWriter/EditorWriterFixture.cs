using System.Diagnostics;
using System.Text.Json;
using NexusPipeline.Platform.Serialization;

internal static class EditorWriterFixture
{
    internal static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments is not [var inputPath, var report]) return 2;
        OwnedFixtureEvidence.RequirePath(inputPath); OwnedFixtureEvidence.RequirePath(report);
        using var document = StrictJson.Parse(File.ReadAllBytes(inputPath));
        var input = document.RootElement;
        if (input.TryGetProperty("child", out var child) && child.GetBoolean())
        {
            var childReport = report + ".child";
            var childInput = inputPath + ".child";
            File.WriteAllBytes(childInput, JsonSerializer.SerializeToUtf8Bytes(new { operations = input.GetProperty("operations") }));
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "configuration-editor-writer", childInput, childReport }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("fixture_editor_child_start_failed");
            var elapsed = Stopwatch.StartNew();
            while (!File.Exists(childReport) && elapsed.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
            if (!File.Exists(childReport)) throw new IOException("fixture_editor_child_not_ready");
        }
        else
        {
            foreach (var operation in input.GetProperty("operations").EnumerateArray())
            {
                var target = operation.GetProperty("target").GetString()!; OwnedFixtureEvidence.RequirePath(target);
                if (operation.TryGetProperty("expectedBefore", out var expected))
                {
                    using var before = StrictJson.Parse(File.ReadAllBytes(target));
                    using var required = StrictJson.Parse(System.Text.Encoding.UTF8.GetBytes(expected.GetString()!));
                    if (!JsonElement.DeepEquals(before.RootElement, required.RootElement)) throw new IOException("fixture_editor_preparation_unbound");
                }
                var kind = operation.GetProperty("kind").GetString()!;
                if (kind is "directory" or "missing")
                {
                    if (File.Exists(target)) File.Delete(target);
                    if (Directory.Exists(target))
                    {
                        foreach (var entry in Directory.EnumerateFileSystemEntries(target, "*", SearchOption.AllDirectories)) OwnedFixtureEvidence.RequirePath(entry);
                        Directory.Delete(target, recursive: true);
                    }
                    if (kind == "directory") { Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "edited.json"), "edited-directory"); }
                }
                else
                {
                    var value = operation.GetProperty("value").GetString()!;
                    if (operation.TryGetProperty("atomic", out var atomic) && atomic.GetBoolean())
                    {
                        var staging = target + ".fixture-new"; OwnedFixtureEvidence.RequirePath(staging);
                        File.WriteAllText(staging, value); File.Move(staging, target, overwrite: true);
                    }
                    else File.WriteAllText(target, value);
                }
            }
        }
        OwnedFixtureEvidence.WriteReport(report, new { status = "edited", processId = Environment.ProcessId });
        await Task.Delay(Timeout.InfiniteTimeSpan); return 0;
    }
}
