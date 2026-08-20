namespace NexusPipeline.Services;

/// <summary>队列完成后等待执行的系统操作及其可取消倒计时。</summary>
internal sealed class PendingSystemAction
{
    public string Action { get; set; } = "";

    public string QueueName { get; set; } = "";

    public DateTime Deadline { get; set; }

    public CancellationTokenSource Cts { get; set; } = new();
}
