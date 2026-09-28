using System.Diagnostics;
using NexusPipeline.Platform.Testing;

namespace NexusPipeline.Modules.Execution;

/// <summary>
/// 一次运行的总时间预算。把 elapsed/remaining/命令上限计算集中在一个值对象中，
/// 避免前置脚本、游戏启动、监控循环和重试各自复制 total timeout 语义。
/// </summary>
internal sealed class RunBudget
{
    private readonly int _timeoutMinutes;
    private readonly DateTime _startedAt;
    private readonly Func<DateTime>? _clock;
    private readonly long _startedStamp;
    private readonly double _initialElapsedSeconds;

    public RunBudget(int timeoutMinutes, DateTime startedAt, Func<DateTime>? clock = null)
    {
        _timeoutMinutes = timeoutMinutes;
        _startedAt = startedAt;
        _clock = clock;
        // Production duration is monotonic. The optional clock remains for
        // deterministic tests and the initial offset preserves a preexisting
        // start timestamp passed by older callers.
        _initialElapsedSeconds = clock is null ? (DateTime.Now - startedAt).TotalSeconds : 0;
        _startedStamp = Stopwatch.GetTimestamp();
    }

    public double ElapsedSeconds => _clock is null
        ? _initialElapsedSeconds + Stopwatch.GetElapsedTime(_startedStamp).TotalSeconds
        : (_clock() - _startedAt).TotalSeconds;

    public double RemainingSeconds
    {
        get
        {
            if (_timeoutMinutes <= 0)
            {
                return double.PositiveInfinity;
            }
            return TestHooks.ScaledSeconds(_timeoutMinutes * 60) - ElapsedSeconds;
        }
    }

    public bool IsExpired => RemainingSeconds <= 0;

    /// <summary>为 adb 等外部命令提供不超过 cap 的剩余超时；无限预算时返回 cap。</summary>
    public int RemainingCommandSeconds(int cap)
    {
        if (double.IsPositiveInfinity(RemainingSeconds))
        {
            return cap;
        }
        return Math.Max(1, Math.Min(cap, (int)Math.Ceiling(RemainingSeconds)));
    }
}
