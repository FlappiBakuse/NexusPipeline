using System.Diagnostics;
using System.Text.Json;

internal static class NativeResumeOutput
{
    internal static async Task<int> RunAsync(string extension, string configuration, string log)
    {
        OwnedFixtureEvidence.RequirePath(configuration); OwnedFixtureEvidence.RequirePath(log);
        var progressPath = Path.Combine(Path.GetDirectoryName(configuration)!, "progress.json");
        OwnedFixtureEvidence.RequirePath(progressPath);
        var bytes = File.ReadAllBytes(configuration);
        using var document = JsonDocument.Parse(bytes);
        using var progress = JsonDocument.Parse(File.ReadAllBytes(progressPath));
        var ordinal = progress.RootElement.GetProperty("ordinal").GetInt32();
        var retry = File.Exists(log + ".attempts");
        File.AppendAllText(log + ".attempts", Convert.ToBase64String(bytes) + "\n");
        File.AppendAllText(log + ".progress", ordinal + "\n");
        if (retry)
        {
            foreach (var id in File.ReadAllLines(log + ".processes").Select(int.Parse))
                try { using var previous = Process.GetProcessById(id); if (!previous.HasExited) return 81; }
                catch (ArgumentException) { }
            if (ordinal != 1 || File.ReadAllLines(log + ".attempts").Distinct(StringComparer.Ordinal).Count() != 1) return 82;
            File.WriteAllText(log + ".restart-verified", "previous family stopped; original selection and latest persisted progress retained");
        }
        IEnumerable<string> lines = extension == "okww" ? OkWw(retry) : Zzz(document.RootElement, retry);
        await File.AppendAllTextAsync(log, string.Join("\n", lines) + "\n");
        if (retry) return 0;
        await File.WriteAllTextAsync(progressPath, "{\"ordinal\":1,\"remaining\":3}");
        var child = InheritedOutputChild.StartQuiet(Environment.ProcessPath!, log + ".game");
        File.WriteAllLines(log + ".processes", [Environment.ProcessId.ToString(), child.ToString()]);
        await Console.Out.WriteLineAsync("NATIVE_TEST_FAILURE"); await Console.Out.FlushAsync();
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 83;
    }

    private static string[] OkWw(bool retry) => retry
        ? new[] { "open_daily", "claim daily reward via  coordinate", "battle pass", "Daily Task Completed" }
            .Select(message => "2026-10-29 00:00:00,000 INFO TaskExecutor DailyTask:" + message).ToArray()
        : ["2026-10-29 00:00:00,000 INFO TaskExecutor DailyTask:open_daily",
            "2026-10-29 00:00:00,000 ERROR TaskExecutor TaskExecutor:📅 Daily Task exception stopped controlled failure"];

    private static List<string> Zzz(JsonElement configuration, bool retry)
    {
        var lines = new List<string>();
        void Message(string text, bool failed = false) => lines.Add("[00:00:00.000] [operation.py 451] [" + (failed ? "ERROR" : "INFO") + "]: " + text);
        void Group(string status, string path = "执行应用") => Message("指令[ 执行应用组 one_dragon ] 节点 " + path + " 返回状态 " + status);
        Group("成功", "获取应用组配置");
        foreach (var app in configuration.GetProperty("app_list").EnumerateArray())
        {
            var title = app.GetProperty("fixture_title").GetString()!;
            if (!app.GetProperty("enabled").GetBoolean()) { Group("应用未启用 " + title); continue; }
            if (retry && app.GetProperty("app_id").GetString() == "email") { Group("应用已完成 " + title); continue; }
            Message("指令[ " + title + " ] 节点 检测游戏窗口 返回状态 成功");
            var failed = !retry && app.GetProperty("app_id").GetString() == "coffee";
            Message("指令[ " + title + " ] 执行" + (failed ? "失败" : "成功") + " 返回状态 " + (failed ? "失败" : "成功"), failed);
            Group("下一个");
        }
        Group("全部结束"); Message("指令[ 执行应用组 one_dragon ] 执行成功 返回状态 成功");
        Message("指令[ 一条龙 ] 执行成功 返回状态 成功");
        return lines;
    }
}
