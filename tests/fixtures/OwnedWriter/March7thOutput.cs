using System.Text;

internal static class March7thOutput
{
    internal static async Task<int> RunAsync(string mode, string configuration, string log)
    {
        OwnedFixtureEvidence.RequirePath(configuration); OwnedFixtureEvidence.RequirePath(log);
        var config = File.ReadAllLines(configuration);
        var selected = config.Where(line => line.EndsWith(": true", StringComparison.Ordinal))
            .Select(line => line[..^6]).Order(StringComparer.Ordinal).ToArray();
        var marker = log + ".attempts"; var retry = File.Exists(marker);
        File.AppendAllText(marker, string.Join(",", selected) + "\n", new UTF8Encoding(false));
        File.AppendAllText(log + ".progress", config.Single(line => line.StartsWith("universe_count: ", StringComparison.Ordinal)) + "\n");
        if (!retry)
            await File.WriteAllTextAsync(configuration, string.Join("\n", config.Select(line =>
                line == "universe_count: 7" ? "universe_count: 8" : line)) + "\n", new UTF8Encoding(false));
        var lines = new List<string>();
        void Info(string text, string level = "INFO") => lines.Add("2026-10-26 12:00:00,123 | " + level + " | " + text);
        if (selected.Contains("daily_enable"))
        {
            lines.Add("| 开始日常任务 |");
            if (!retry && mode == "march7th-roots") Info("当前模块执行失败", "ERROR");
            lines.Add("--- 完成 ---");
        }
        if (selected.Contains("forgottenhall_enable"))
        { lines.Add("| 准备忘却之庭 |"); lines.Add("--- 完成 ---"); }
        if (selected.Contains("reward_enable"))
        {
            lines.Add("| 开始领取奖励 |"); lines.Add("=== 检测到邮件奖励 ===");
            if (!retry) Info("邮件领取失败", "ERROR");
            lines.Add("--- 邮件奖励完成 ---"); lines.Add("--- 完成 ---");
        }
        lines.Add("| 停止运行 |");
        await File.AppendAllTextAsync(log, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        await Console.Out.WriteLineAsync("Controlled March7th log fixture completed");
        return retry ? 0 : 37;
    }
}
