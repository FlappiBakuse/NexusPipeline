using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Update;

/// <summary>
/// 常驻宿主启动后的单次自动更新检查协调器。它只负责设置门禁、启动延迟和生命周期日志，
/// release 解析、网络访问与状态机仍由 UpdateService 独立实现。
/// </summary>
internal sealed class StartupUpdateCheckService
{
    private readonly Func<bool> _isEnabled;
    private readonly Func<Task<UpdateStatusSnapshot>> _check;
    private readonly Func<TimeSpan, Task> _delay;
    private int _started;

    internal StartupUpdateCheckService(
        Func<bool> isEnabled,
        Func<UpdateService> updateService,
        Func<TimeSpan, Task>? delay = null)
        : this(
            isEnabled,
            () => updateService().CheckAsync(Audit.System),
            delay)
    {
    }

    internal StartupUpdateCheckService(
        Func<bool> isEnabled,
        Func<Task<UpdateStatusSnapshot>> check,
        Func<TimeSpan, Task>? delay = null)
    {
        _isEnabled = isEnabled;
        _check = check;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>安排一次后台检查；重复调用不会产生第二次检查。</summary>
    internal void Start()
    {
        _ = StartAsync();
    }

    /// <summary>可等待的启动入口，供生命周期测试使用。</summary>
    internal Task StartAsync()
    {
        return Interlocked.Exchange(ref _started, 1) == 0
            ? RunAsync()
            : Task.CompletedTask;
    }

    private async Task RunAsync()
    {
        try
        {
            if (!_isEnabled())
            {
                Logger.Info("[更新] 启动自动检查未启用");
                return;
            }

            TimeSpan delay = TimeSpan.FromMilliseconds(TestHooks.ScaledMs(5000));
            Logger.Info($"[更新] 已安排启动自动检查（延迟 {delay.TotalMilliseconds:0}ms）");
            await _delay(delay).ConfigureAwait(false);
            Logger.Info("[更新] 开始启动自动检查");
            UpdateStatusSnapshot status = await _check().ConfigureAwait(false);
            Logger.Info($"[更新] 启动自动检查完成（状态={status.State}，可用更新={status.Available}）");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[更新] 启动自动检查失败：{ex.Message}");
        }
    }
}
