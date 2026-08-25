namespace NexusPipeline.Models;

public class QueueTimeSet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public bool Enabled { get; set; } = true;

    public List<int> Days { get; set; } = new();

    public string Time { get; set; } = "08:00";
}

public class QueueTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public int Index { get; set; }

    public string ScriptInstanceId { get; set; } = "";
}

public class DispatchQueue
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    /// <summary>列表展示顺序（拖拽排序落盘；新建追加为当前最大值 +1）。</summary>
    public int Index { get; set; }

    public string AutoRunMode { get; set; } = "none";

    public string CompletionAction { get; set; } = "none";

    public List<QueueTimeSet> TimeSets { get; set; } = new();

    public List<QueueTask> Tasks { get; set; } = new();

    public bool NotifyEnabled { get; set; }

    private static readonly System.Text.Json.JsonSerializerOptions CloneOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>深拷贝（改序列化往返，避免手工逐字段复制随新增字段漂移）。</summary>
    public DispatchQueue Clone()
    {
        return System.Text.Json.JsonSerializer.Deserialize<DispatchQueue>(
            System.Text.Json.JsonSerializer.Serialize(this, CloneOptions), CloneOptions) ?? new DispatchQueue();
    }
}

internal static class QueueRule
{
    public static bool IsValidAutoRunMode(string mode)
    {
        return mode is "startup" or "scheduled" or "none";
    }

    public static bool IsValidCompletionAction(string action)
    {
        return action is "none" or "exit" or "sleep" or "reboot" or "shutdown";
    }

    public static string CompletionActionDesc(string action)
    {
        return action switch
        {
            "exit" => "退出软件",
            "sleep" => "休眠",
            "reboot" => "重启",
            "shutdown" => "关机",
            _ => "无操作",
        };
    }

    public static string AutoRunModeDesc(string mode)
    {
        return mode switch
        {
            "startup" => "启动时运行",
            "none" => "不运行",
            _ => "定时运行",
        };
    }
}
