namespace NexusPipeline.Cli;

/// <summary>历史记录子菜单：最近记录列表 + 运行详情。</summary>
internal static class HistoryMenu
{
    public static void Show(RuntimeContext ctx)
    {
        Ui.ClearScreen();
        List<RunRecord> records = ctx.History.Recent(ctx.Settings.HistoryRetentionDays);
        Console.WriteLine("===== 历史记录（最近 {0} 天，共 {1} 条） =====", ctx.Settings.HistoryRetentionDays, records.Count);
        for (int i = 0; i < records.Count; i++)
        {
            RunRecord record = records[i];
            string status = record.Status switch
            {
                "success" => "成功",
                "cancelled" => "已取消",
                _ => "失败",
            };
            Console.WriteLine($"  {i + 1}. {record.StartTime:MM-dd HH:mm} {record.ScriptName} [{status}]（{record.ResultDetail}）{(string.IsNullOrEmpty(record.QueueName) ? "" : $" 队列：{record.QueueName}")}");
        }
        Console.WriteLine();
        Console.WriteLine("输入序号查看详情，回车返回：");
        string? choice = Ui.Prompt("");
        if (int.TryParse(choice?.Trim(), out int index) && index >= 1 && index <= records.Count)
        {
            ShowRecordDetail(records[index - 1]);
        }
    }

    private static void ShowRecordDetail(RunRecord record)
    {
        Ui.ClearScreen();
        Console.WriteLine($"===== {record.ScriptName} 运行详情 =====");
        Console.WriteLine($"模式：{(record.Mode == "auto" ? "自动运行" : "手动运行")} | 队列：{(string.IsNullOrEmpty(record.QueueName) ? "-" : record.QueueName)}");
        Console.WriteLine($"开始：{record.StartTime:yyyy-MM-dd HH:mm:ss} | 结束：{record.EndTime:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"最终状态：{record.Status} | {record.ResultDetail}");
        foreach (RunAttempt attempt in record.AttemptDetails)
        {
            Console.WriteLine();
            Console.WriteLine($"--- 第 {attempt.Number} 次尝试：{attempt.Status} ---");
            Console.WriteLine($"原因：{attempt.Reason}");
            Console.WriteLine($"时间：{attempt.StartTime:HH:mm:ss} - {attempt.EndTime:HH:mm:ss}");
            if (attempt.LogTail.Count > 0)
            {
                Console.WriteLine("日志尾部：");
                foreach (string line in attempt.LogTail.TakeLast(10))
                {
                    Console.WriteLine($"  {line}");
                }
            }
            if (!string.IsNullOrWhiteSpace(attempt.OutputTail))
            {
                Console.WriteLine("控制台输出尾部：");
                foreach (string line in attempt.OutputTail.Split('\n').TakeLast(10))
                {
                    Console.WriteLine($"  {line}");
                }
            }
        }
        Console.WriteLine();
        Console.Write("按回车返回...");
        Console.ReadLine();
    }
}
