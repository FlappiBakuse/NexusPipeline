namespace NexusPipeline.Models;

public class RunAttempt
{
    public int Number { get; set; }

    public DateTime StartTime { get; set; }

    public DateTime? EndTime { get; set; }

    public string Status { get; set; } = "";

    public string Reason { get; set; } = "";

    /// <summary>本尝试脚本日志文件名（如 HH-mm-ss-1.log，按尝试分批落盘，v0.5.3+）。</summary>
    public string LogFile { get; set; } = "";
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

    public DateTime StartTime { get; set; }

    public DateTime? EndTime { get; set; }

    public int Attempts { get; set; }

    public int MaxAttempts { get; set; }

    public string Status { get; set; } = "running";

    public string FinalStatus { get; set; } = "";

    public string ResultDetail { get; set; } = "";

    /// <summary>判断脚本返回的自定义通知文本（仅本次运行有效，不落盘历史）；为空则通知使用默认正文。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string CustomNotifyText { get; set; } = "";

    public string LogFile { get; set; } = "";

    public List<RunAttempt> AttemptDetails { get; set; } = new();

    public RunRecord Clone()
    {
        return new RunRecord
        {
            Id = Id,
            ScriptInstanceId = ScriptInstanceId,
            ScriptName = ScriptName,
            QueueId = QueueId,
            QueueName = QueueName,
            Mode = Mode,
            UserName = UserName,
            StartTime = StartTime,
            EndTime = EndTime,
            Attempts = Attempts,
            MaxAttempts = MaxAttempts,
            Status = Status,
            FinalStatus = FinalStatus,
            ResultDetail = ResultDetail,
            LogFile = LogFile,
            AttemptDetails = AttemptDetails.Select(a => new RunAttempt
            {
                Number = a.Number,
                StartTime = a.StartTime,
                EndTime = a.EndTime,
                Status = a.Status,
                Reason = a.Reason,
                LogFile = a.LogFile,
            }).ToList(),
        };
    }
}
