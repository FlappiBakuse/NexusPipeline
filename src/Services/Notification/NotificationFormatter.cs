using NexusPipeline.Models;

namespace NexusPipeline.Services.Notification;

internal static class NotificationFormatter
{
    public static string Script(ScriptInstance script, RunRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.CustomNotifyText))
        {
            return record.CustomNotifyText;
        }
        string mode = record.Mode == "auto" ? "自动运行" : "手动运行";
        string status = record.Status switch
        {
            "success" => $"运行成功（{record.ResultDetail}）",
            "partial" => $"运行部分完成（{record.ResultDetail}）",
            "cancelled" => "运行已取消",
            "skipped" => $"运行已跳过（{record.ResultDetail}）",
            _ => $"运行失败（{record.ResultDetail}）",
        };
        var lines = new List<string>
        {
            $"[NexusPipeline] 脚本「{script.Name}」",
            $"运行方式：{mode}",
            $"开始时间：{record.StartTime:yyyy-MM-dd HH:mm:ss}",
        };
        if (record.EndTime is not null)
        {
            lines.Add($"结束时间：{record.EndTime:yyyy-MM-dd HH:mm:ss}");
        }
        if (!string.IsNullOrWhiteSpace(record.UserName))
        {
            lines.Add($"用户：{record.UserName}");
        }
        lines.Add($"尝试次数：{record.Attempts}");
        lines.Add($"最终状态：{status}");
        return string.Join("\r\n", lines);
    }

    public static string Queue(DispatchQueue queue, IReadOnlyList<RunRecord> records)
    {
        var lines = new List<string>
        {
            $"[NexusPipeline] 调度队列「{queue.Name}」运行汇总",
            $"任务总数：{records.Count}",
            "",
        };
        foreach (RunRecord record in records)
        {
            string status = record.Status switch
            {
                "success" => $"成功（{record.ResultDetail}）",
                "partial" => $"部分完成（{record.ResultDetail}）",
                "cancelled" => "已取消",
                "skipped" => $"已跳过（{record.ResultDetail}）",
                _ => $"失败（{record.ResultDetail}）",
            };
            lines.Add($"· {record.ScriptName}：{status}");
        }
        return string.Join("\r\n", lines);
    }
}
