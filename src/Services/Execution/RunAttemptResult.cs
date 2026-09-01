using NexusPipeline.Services.Execution;

namespace NexusPipeline.Services;

/// <summary>一次尝试的结果值对象；保留原有状态字符串和致命/取消语义。</summary>
internal sealed class RunAttemptResult
{
    public string Status { get; set; } = "";
    public string Reason { get; set; } = "";
    public bool IsFatal { get; set; }
    public string NotifyText { get; set; } = "";
    public string NotifyScreenshotId { get; set; } = "";

    public static RunAttemptResult Success(string reason) => new() { Status = "success", Reason = reason };
    public static RunAttemptResult Failed(string reason) => new() { Status = "failed", Reason = reason };
    public static RunAttemptResult Fatal(string reason) => new() { Status = "failed", Reason = reason, IsFatal = true };
    public static RunAttemptResult Cancelled(string reason) => new() { Status = "cancelled", Reason = reason, IsFatal = true };

    /// <summary>
    /// 合并主脚本与后置脚本结果。主脚本的致命性、判定原因与通知文本拥有优先级；
    /// 后置脚本失败仍会让原本成功的尝试失败，但不能把主脚本的 fatal/Judge 结论改写掉。
    /// </summary>
    public static RunAttemptResult MergePostRun(RunAttemptResult main, RunAttemptResult post)
    {
        bool postFailed = post.Status is "failed" or "cancelled";
        bool mainFailed = main.Status is "failed" or "cancelled";
        var merged = new RunAttemptResult
        {
            Status = main.Status,
            Reason = main.Reason,
            IsFatal = main.IsFatal,
            NotifyText = string.IsNullOrWhiteSpace(main.NotifyText) ? post.NotifyText : main.NotifyText,
            NotifyScreenshotId = string.IsNullOrWhiteSpace(main.NotifyScreenshotId)
                ? post.NotifyScreenshotId
                : main.NotifyScreenshotId,
        };

        if (!string.IsNullOrWhiteSpace(post.Reason)
            && postFailed
            && !string.Equals(post.Reason, main.Reason, StringComparison.Ordinal))
        {
            merged.Reason = string.IsNullOrWhiteSpace(main.Reason)
                ? post.Reason
                : $"{main.Reason}；后置脚本：{post.Reason}";
        }

        if (!mainFailed && postFailed)
        {
            merged.Status = post.Status;
            merged.IsFatal = post.IsFatal;
        }
        return merged;
    }
}

/// <summary>一次尝试结束后判断是否已到实际最终尝试，供 PostRunOnFinalOnly 使用。</summary>
internal static class AttemptLifecycle
{
    public static bool ShouldRunPreRun(bool hasScript, bool onceOnly, bool completedSuccessfully)
    {
        return hasScript && (!onceOnly || !completedSuccessfully);
    }

    public static bool ShouldRunPostRun(bool finalOnly, int attemptNumber, RetryPolicy policy, RunAttemptResult mainResult)
    {
        return !finalOnly || !policy.ShouldRetry(attemptNumber, mainResult);
    }
}
