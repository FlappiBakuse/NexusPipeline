using NexusPipeline.Models;
using NexusPipeline.Localization;

namespace NexusPipeline.Services.Notification;

internal static class NotificationFormatter
{
    public static string Script(ScriptInstance script, RunRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.CustomNotifyText))
        {
            return record.CustomNotifyText;
        }
        bool english = LocaleContext.Current == LocaleCatalog.EnglishLocale;
        string mode = english
            ? (record.Mode == "auto" ? "Automatic" : "Manual")
            : (record.Mode == "auto" ? "自动运行" : "手动运行");
        string detail = RunResultLocalization.Detail(record);
        string status = record.Status switch
        {
            "success" => english ? $"Run succeeded ({detail})" : $"运行成功（{detail}）",
            "partial" => english ? $"Run partially completed ({detail})" : $"运行部分完成（{detail}）",
            "cancelled" => english ? "Run cancelled" : "运行已取消",
            "skipped" => english ? $"Run skipped ({detail})" : $"运行已跳过（{detail}）",
            _ => english ? $"Run failed ({detail})" : $"运行失败（{detail}）",
        };
        var lines = new List<string>
        {
            english ? $"[NexusPipeline] Script \"{script.Name}\"" : $"[NexusPipeline] 脚本「{script.Name}」",
            english ? $"Run mode: {mode}" : $"运行方式：{mode}",
            english ? $"Started: {record.StartTime:yyyy-MM-dd HH:mm:ss}" : $"开始时间：{record.StartTime:yyyy-MM-dd HH:mm:ss}",
        };
        if (record.EndTime is not null)
        {
            lines.Add(english
                ? $"Ended: {record.EndTime:yyyy-MM-dd HH:mm:ss}"
                : $"结束时间：{record.EndTime:yyyy-MM-dd HH:mm:ss}");
        }
        if (!string.IsNullOrWhiteSpace(record.UserName))
        {
            lines.Add(english ? $"User: {record.UserName}" : $"用户：{record.UserName}");
        }
        lines.Add(english ? $"Attempts: {record.Attempts}" : $"尝试次数：{record.Attempts}");
        lines.Add(english ? $"Final status: {status}" : $"最终状态：{status}");
        return string.Join("\r\n", lines);
    }

    public static string Queue(DispatchQueue queue, IReadOnlyList<RunRecord> records)
    {
        bool english = LocaleContext.Current == LocaleCatalog.EnglishLocale;
        var lines = new List<string>
        {
            english ? $"[NexusPipeline] Queue \"{queue.Name}\" run summary" : $"[NexusPipeline] 调度队列「{queue.Name}」运行汇总",
            english ? $"Total tasks: {records.Count}" : $"任务总数：{records.Count}",
            "",
        };
        foreach (RunRecord record in records)
        {
            string detail = RunResultLocalization.Detail(record);
            string status = record.Status switch
            {
                "success" => english ? $"Succeeded ({detail})" : $"成功（{detail}）",
                "partial" => english ? $"Partially completed ({detail})" : $"部分完成（{detail}）",
                "cancelled" => english ? "Cancelled" : "已取消",
                "skipped" => english ? $"Skipped ({detail})" : $"已跳过（{detail}）",
                _ => english ? $"Failed ({detail})" : $"失败（{detail}）",
            };
            lines.Add(english ? $"· {record.ScriptName}: {status}" : $"· {record.ScriptName}：{status}");
        }
        return string.Join("\r\n", lines);
    }
}
