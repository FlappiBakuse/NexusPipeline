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
        string mode = HostLocalization.TranslateNamed(
            record.Mode == "auto" ? "notification.mode.auto" : "notification.mode.manual",
            record.Mode == "auto" ? "自动运行" : "手动运行");
        string detail = RunResultLocalization.Detail(record);
        string status = record.Status switch
        {
            "success" => HostLocalization.TranslateNamed("notification.script.success", $"运行成功（{detail}）", new Dictionary<string, object?> { ["detail"] = detail }),
            "partial" => HostLocalization.TranslateNamed("notification.script.partial", $"运行部分完成（{detail}）", new Dictionary<string, object?> { ["detail"] = detail }),
            "cancelled" => HostLocalization.TranslateNamed("notification.script.cancelled", "运行已取消"),
            "skipped" => HostLocalization.TranslateNamed("notification.script.skipped", $"运行已跳过（{detail}）", new Dictionary<string, object?> { ["detail"] = detail }),
            _ => HostLocalization.TranslateNamed("notification.script.failed", $"运行失败（{detail}）", new Dictionary<string, object?> { ["detail"] = detail }),
        };
        var lines = new List<string>
        {
            HostLocalization.TranslateNamed("notification.script.title", $"[NexusPipeline] 脚本「{script.Name}」", new Dictionary<string, object?> { ["name"] = script.Name }),
            HostLocalization.TranslateNamed("notification.script.mode", $"运行方式：{mode}", new Dictionary<string, object?> { ["mode"] = mode }),
            HostLocalization.TranslateNamed("notification.script.started", $"开始时间：{record.StartTime:yyyy-MM-dd HH:mm:ss}", new Dictionary<string, object?> { ["time"] = record.StartTime.ToString("yyyy-MM-dd HH:mm:ss") }),
        };
        if (record.EndTime is not null)
        {
            lines.Add(HostLocalization.TranslateNamed("notification.script.ended", $"结束时间：{record.EndTime:yyyy-MM-dd HH:mm:ss}", new Dictionary<string, object?> { ["time"] = record.EndTime.Value.ToString("yyyy-MM-dd HH:mm:ss") }));
        }
        if (!string.IsNullOrWhiteSpace(record.UserName))
        {
            lines.Add(HostLocalization.TranslateNamed("notification.script.user", $"用户：{record.UserName}", new Dictionary<string, object?> { ["name"] = record.UserName }));
        }
        lines.Add(HostLocalization.TranslateNamed("notification.script.attempts", $"尝试次数：{record.Attempts}", new Dictionary<string, object?> { ["count"] = record.Attempts }));
        lines.Add(HostLocalization.TranslateNamed("notification.script.final", $"最终状态：{status}", new Dictionary<string, object?> { ["status"] = status }));
        return string.Join("\r\n", lines);
    }

    public static string Queue(DispatchQueue queue, IReadOnlyList<RunRecord> records)
    {
        var lines = new List<string>
        {
            HostLocalization.TranslateNamed("notification.queue.title", $"[NexusPipeline] 调度队列「{queue.Name}」运行汇总", new Dictionary<string, object?> { ["name"] = queue.Name }),
            HostLocalization.TranslateNamed("notification.queue.total", $"任务总数：{records.Count}", new Dictionary<string, object?> { ["count"] = records.Count }),
            "",
        };
        foreach (RunRecord record in records)
        {
            string detail = RunResultLocalization.Detail(record);
            string status = record.Status switch
            {
                "success" => HostLocalization.TranslateNamed("notification.queue.success", $"成功（{detail}）", new Dictionary<string, object?> { ["detail"] = detail }),
                "partial" => HostLocalization.TranslateNamed("notification.queue.partial", $"部分完成（{detail}）", new Dictionary<string, object?> { ["detail"] = detail }),
                "cancelled" => HostLocalization.TranslateNamed("notification.queue.cancelled", "已取消"),
                "skipped" => HostLocalization.TranslateNamed("notification.queue.skipped", $"已跳过（{detail}）", new Dictionary<string, object?> { ["detail"] = detail }),
                _ => HostLocalization.TranslateNamed("notification.queue.failed", $"失败（{detail}）", new Dictionary<string, object?> { ["detail"] = detail }),
            };
            lines.Add(HostLocalization.TranslateNamed("notification.queue.item", $"· {record.ScriptName}：{status}", new Dictionary<string, object?> { ["name"] = record.ScriptName, ["status"] = status }));
        }
        return string.Join("\r\n", lines);
    }
}
