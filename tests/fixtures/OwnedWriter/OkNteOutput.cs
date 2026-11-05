using System.Diagnostics;
using System.Text.Json;

internal static class OkNteOutput
{
    internal static async Task<int> RunAsync(string configuration, string log)
    {
        OwnedFixtureEvidence.RequirePath(configuration); OwnedFixtureEvidence.RequirePath(log);
        using var document = JsonDocument.Parse(File.ReadAllBytes(configuration));
        var selected = document.RootElement.GetProperty("Routine Items").EnumerateArray()
            .Where(item => item.GetProperty("enabled").GetBoolean()).Select(item => item.GetProperty("id").GetString()!).ToArray();
        var retry = File.Exists(log + ".attempts");
        if (retry)
        {
            foreach (var id in File.ReadAllLines(log + ".processes").Select(int.Parse))
                try { using var previous = Process.GetProcessById(id); if (!previous.HasExited) return 81; }
                catch (ArgumentException) { }
            if (!selected.SequenceEqual(["daily_claim"])) return 82;
            File.WriteAllText(log + ".restart-verified", "previous family stopped; only failed business remains selected");
        }
        File.AppendAllText(log + ".attempts", JsonSerializer.Serialize(selected) + "\n");
        var lines = new List<string>();
        void Info(string message) => lines.Add("2026-10-29 00:00:00,000 INFO TaskExecutor DailyRoutineTask:" + message);
        Info("开始执行日常任务");
        foreach (var id in selected)
        {
            var title = id == "coffee" ? "一咖舍" : "日常领取";
            Info("开始任务: " + title);
            Info((id == "daily_claim" && !retry ? "任务失败: " : "任务完成: ") + title);
        }
        Info("结束执行日常任务");
        await File.AppendAllTextAsync(log, string.Join("\n", lines) + "\n");
        if (retry) return 0;
        var child = InheritedOutputChild.StartQuiet(Environment.ProcessPath!, log + ".game");
        File.WriteAllLines(log + ".processes", [Environment.ProcessId.ToString(), child.ToString()]);
        await Console.Out.WriteLineAsync("OKNTE_TEST_FAILURE"); await Console.Out.FlushAsync();
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 83;
    }
}
