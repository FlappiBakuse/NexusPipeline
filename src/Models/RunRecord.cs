namespace NexusPipeline.Models;

public class RunAttempt
{
    public int Number { get; set; }

    public DateTime StartTime { get; set; }

    public DateTime? EndTime { get; set; }

    public string Status { get; set; } = "";

    public string Reason { get; set; } = "";

    /// <summary>本尝试脚本日志文件名（如 HH-mm-ss-1.log，按尝试分批落盘）。</summary>
    public string LogFile { get; set; } = "";

    /// <summary>本次尝试最终保留的截图元数据；截图本体与本尝试日志存放在同一运行目录。</summary>
    public List<RunHistoryScreenshot> Screenshots { get; set; } = new();
}

public class RunHistoryScreenshot
{
    /// <summary>运行期截图 ID，供判断脚本选择通知图片。</summary>
    public string Id { get; set; } = "";

    /// <summary>运行目录内的截图文件名。</summary>
    public string FileName { get; set; } = "";

    public DateTimeOffset CapturedAt { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public string Source { get; set; } = "";

    public string Trigger { get; set; } = "";

    /// <summary>本次尝试内按采集顺序递增的序号。</summary>
    public long Ordinal { get; set; }
}

public class RunRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string ScriptInstanceId { get; set; } = "";

    public string ScriptName { get; set; } = "";

    public string QueueId { get; set; } = "";

    public string QueueName { get; set; } = "";

    public string Mode { get; set; } = "";

    public string UserName { get; set; } = "";

    /// <summary>运行时冻结的全局用户身份。</summary>
    public string UserId { get; set; } = "";

    /// <summary>本次运行使用的插件/解析快照诊断信息，不参与下次运行配置。</summary>
    public string PluginVersion { get; set; } = "";

    public string ProfileHash { get; set; } = "";

    public string JudgeSourceKind { get; set; } = "";

    public string JudgeHash { get; set; } = "";

    public DateTime StartTime { get; set; }

    public DateTime? EndTime { get; set; }

    public int Attempts { get; set; }

    public int MaxAttempts { get; set; }

    public string Status { get; set; } = "running";

    public string FinalStatus { get; set; } = "";

    public string ResultDetail { get; set; } = "";

    /// <summary>相对于当天 history 目录的运行目录，例如「张三\\14-58-21」。</summary>
    public string HistoryDirectory { get; set; } = "";

    /// <summary>判断脚本返回的自定义通知文本（仅本次运行有效，不落盘历史）；为空则通知使用默认正文。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string CustomNotifyText { get; set; } = "";

    /// <summary>运行期通知选择的截图 ID（仅内存）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string NotifyScreenshotId { get; set; } = "";

    public string LogFile { get; set; } = "";

    public List<RunAttempt> AttemptDetails { get; set; } = new();

    /// <summary>运行落盘前由插件生成的展示快照；不影响 Status、FinalStatus 或执行流程。</summary>
    public List<PluginHistoryRecord> PluginHistory { get; set; } = new();

    private static readonly System.Text.Json.JsonSerializerOptions CloneOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>深拷贝（改序列化往返，避免手工逐字段复制随新增字段漂移；CustomNotifyText 与历史行为一致不复制）。</summary>
    public RunRecord Clone()
    {
        return System.Text.Json.JsonSerializer.Deserialize<RunRecord>(
            System.Text.Json.JsonSerializer.Serialize(this, CloneOptions), CloneOptions) ?? new RunRecord();
    }
}
