namespace NexusPipeline.Services;

/// <summary>队列完成后等待执行的系统操作及其可取消倒计时。</summary>
internal sealed class PendingSystemAction
{
    public string Action { get; set; } = "";

    public string QueueName { get; set; } = "";

    public DateTime Deadline { get; set; }

    public CancellationTokenSource Cts { get; set; } = new();

    /// <summary>系统调用已完成状态转换；状态锁之外执行实际 OS side effect。</summary>
    internal bool IsArmed { get; set; }
}
