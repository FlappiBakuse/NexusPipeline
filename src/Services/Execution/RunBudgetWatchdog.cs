namespace NexusPipeline.Services.Execution;

/// <summary>
/// 独立运行总预算 watchdog。它不依赖 AttemptMonitor 的下一次 tick，
/// 到达 deadline 时通过回调取消所有可中断的非 cleanup 工作。
/// </summary>
internal sealed class RunBudgetWatchdog : IAsyncDisposable
{
    private readonly RunBudget _budget;
    private readonly CancellationToken _runToken;
    private readonly Action _onExpired;
    private readonly CancellationTokenSource _stopCts = new();
    private Task? _task;

    public RunBudgetWatchdog(RunBudget budget, CancellationToken runToken, Action onExpired)
    {
        _budget = budget;
        _runToken = runToken;
        _onExpired = onExpired;
    }

    public void Start()
    {
        if (_task is not null)
        {
            throw new InvalidOperationException("RunBudget watchdog 已启动");
        }
        _task = WatchAsync();
    }

    private async Task WatchAsync()
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_runToken, _stopCts.Token);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                double remaining = _budget.RemainingSeconds;
                if (remaining <= 0)
                {
                    _onExpired();
                    return;
                }

                // -1 表示不设总时间上限，RunBudget 用 PositiveInfinity 表示该状态。
                // Task.Delay 不接受由 infinity 转换得到的 TimeSpan；此时保持等待，
                // 直到运行取消或 DisposeAsync 发出停止信号。
                if (double.IsPositiveInfinity(remaining))
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token).ConfigureAwait(false);
                    return;
                }

                // 保持转换在 TimeSpan 可表示范围内，避免极大配置值在清理 watchdog
                // 时因 TimeSpan.FromSeconds 溢出而把一次成功运行改写为失败。
                double delaySeconds = Math.Min(remaining, TimeSpan.MaxValue.TotalSeconds - 1);
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), linked.Token).ConfigureAwait(false);
                if (_budget.IsExpired && !linked.IsCancellationRequested)
                {
                    _onExpired();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopCts.Cancel();
        if (_task is not null)
        {
            await _task.ConfigureAwait(false);
        }
        _stopCts.Dispose();
    }
}
