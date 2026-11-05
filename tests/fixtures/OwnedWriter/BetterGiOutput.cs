using System.Text;

internal static class BetterGiOutput
{
    internal static async Task<int> RunAsync(string mode, string configuration, string log)
    {
        OwnedFixtureEvidence.RequirePath(configuration); OwnedFixtureEvidence.RequirePath(log);
        File.WriteAllText(configuration, "{\"TaskEnabledList\":{\"mail\":false},\"TaskDefinitions\":{\"mail\":\"领取邮件\"},\"CompletionAction\":\"None\"}", new UTF8Encoding(false));
        if (mode == "bettergi-output-invalid") { File.WriteAllBytes(log, [255, 10]); return 37; }
        var context = "Main:S1:P" + (Environment.ProcessId + (mode == "bettergi-output-foreign" ? 1 : 0)) + ":T1";
        var text = new StringBuilder();
        void Message(string logger, string value, string level = "INF") => text.Append("[12:00:00.000] [")
            .Append(level).Append("] [").Append(context).Append("] BetterGenshinImpact.").Append(logger).Append('\n').Append(value).Append('\n');
        Message("ViewModel.Pages.OneDragonFlowViewModel", "一条龙任务执行: 1/1");
        Message("GameTask.TaskRunner", "→ 任务启动！");
        for (var index = 0; index < 400; index++) Message("Other.Logger", "bounded unrelated event " + index);
        if (mode != "bettergi-mail-succeeded") Message("GameTask.TaskRunner", "领取邮件奖励异常: controlled installed test", "ERR");
        Message("GameTask.TaskRunner", "→ 任务结束");
        Message("ViewModel.Pages.OneDragonFlowViewModel", "一条龙和配置组任务结束");
        File.WriteAllText(log, text.ToString(), new UTF8Encoding(false));
        await Console.OpenStandardOutput().WriteAsync("unbound pipe output cannot replace the declared file log\n"u8.ToArray());
        return mode == "bettergi-mail-succeeded" ? 0 : 37;
    }
}
