namespace NexusPipeline;

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

    public string UserName { get; set; } = "";
}

public class DispatchQueue
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    public string AutoRunMode { get; set; } = "none";

    public string CompletionAction { get; set; } = "none";

    public List<QueueTimeSet> TimeSets { get; set; } = new();

    public List<QueueTask> Tasks { get; set; } = new();

    public bool NotifyEnabled { get; set; }

    public DispatchQueue Clone()
    {
        return new DispatchQueue
        {
            Id = Id,
            Name = Name,
            AutoRunMode = AutoRunMode,
            CompletionAction = CompletionAction,
            TimeSets = TimeSets.Select(t => new QueueTimeSet
            {
                Id = t.Id,
                Enabled = t.Enabled,
                Days = new List<int>(t.Days),
                Time = t.Time,
            }).ToList(),
            Tasks = Tasks.Select(t => new QueueTask
            {
                Id = t.Id,
                Index = t.Index,
                ScriptInstanceId = t.ScriptInstanceId,
                UserName = t.UserName,
            }).ToList(),
            NotifyEnabled = NotifyEnabled,
        };
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
