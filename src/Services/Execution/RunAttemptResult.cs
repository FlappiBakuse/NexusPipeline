namespace NexusPipeline.Services;

/// <summary>一次尝试的结果值对象；保留原有状态字符串和致命/取消语义。</summary>
internal sealed class RunAttemptResult
{
    public string Status { get; set; } = "";
    public string Reason { get; set; } = "";
    public bool IsFatal { get; set; }
    public string NotifyText { get; set; } = "";

    public static RunAttemptResult Success(string reason) => new() { Status = "success", Reason = reason };
    public static RunAttemptResult Failed(string reason) => new() { Status = "failed", Reason = reason };
    public static RunAttemptResult Fatal(string reason) => new() { Status = "failed", Reason = reason, IsFatal = true };
    public static RunAttemptResult Cancelled(string reason) => new() { Status = "cancelled", Reason = reason, IsFatal = true };
}
