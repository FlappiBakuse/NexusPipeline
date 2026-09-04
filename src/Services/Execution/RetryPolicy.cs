namespace NexusPipeline.Services.Execution;

using NexusPipeline.Services;

/// <summary>运行级重试策略：只允许普通失败进入下一次尝试，致命/取消/成功/部分完成均不重试。</summary>
internal sealed class RetryPolicy
{
    public RetryPolicy(int configuredMaxAttempts)
    {
        MaxAttempts = Math.Max(1, configuredMaxAttempts);
    }

    public int MaxAttempts { get; }

    public bool ShouldRetry(int attemptNumber, RunAttemptResult result)
    {
        return result.Status == "failed" && !result.IsFatal && attemptNumber < MaxAttempts;
    }
}
