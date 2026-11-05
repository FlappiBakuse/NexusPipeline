using System.Diagnostics;
using System.Text.Json;

internal static class BetterGiRetryOutput
{
    internal static async Task<int> RunAsync(string configuration, string log)
    {
        OwnedFixtureEvidence.RequirePath(configuration); OwnedFixtureEvidence.RequirePath(log);
        using var document = JsonDocument.Parse(File.ReadAllBytes(configuration));
        var enabled = document.RootElement.GetProperty("TaskEnabledList");
        var selected = document.RootElement.GetProperty("TaskOrder").EnumerateArray().Select(item => item.GetString()!)
            .Where(id => enabled.GetProperty(id).GetBoolean()).ToArray();
        var retry = File.Exists(log + ".attempts");
        if (retry)
        {
            foreach (var id in File.ReadAllLines(log + ".processes").Select(int.Parse))
                try { using var previous = Process.GetProcessById(id); if (!previous.HasExited) return 81; }
                catch (ArgumentException) { }
            if (!selected.SequenceEqual(["resin"])) return 82;
            File.WriteAllText(log + ".restart-verified", "previous family stopped; original entry retains startup before selected tasks");
        }
        File.AppendAllText(log + ".attempts", JsonSerializer.Serialize(selected) + "\n");
        var lines = new List<string>();
        void Message(string logger, string message, string level = "INF")
        {
            lines.Add($"[12:00:00.123] [{level}] [Primary:S1:P{Environment.ProcessId}:T1792900000000] BetterGenshinImpact.{logger}");
            lines.Add(message);
        }
        for (var index = 0; index < selected.Length; index++)
        {
            Message("ViewModel.Pages.OneDragonFlowViewModel", $"一条龙任务执行: {index + 1}/{selected.Length}");
            Message("GameTask.TaskRunner", "→ \"任务启动！\"");
            if (selected[index] == "resin" && !retry) Message("GameTask.TaskRunner", "受控业务错误", "ERR");
            Message("GameTask.TaskRunner", "→ \"任务结束\"");
        }
        await File.AppendAllTextAsync(log, string.Join("\n", lines) + "\n");
        if (retry) return 0;
        var child = InheritedOutputChild.StartQuiet(Environment.ProcessPath!, log + ".game");
        File.WriteAllLines(log + ".processes", [Environment.ProcessId.ToString(), child.ToString()]);
        await Console.Out.WriteLineAsync("BETTERGI_TEST_FAILURE"); await Console.Out.FlushAsync();
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 83;
    }
}
