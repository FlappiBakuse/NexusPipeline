using NexusPipeline.Services.Execution;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Update;

/// <summary>自动更新闲时判定结果；成功时持有的维护租约由应用入口接管。</summary>
internal sealed record AutoUpdateIdleAttempt(
    HostMaintenanceLease? Lease,
    AutoUpdateIdleBlocker? Blocker)
{
    public bool Acquired => Lease is not null;

    public static AutoUpdateIdleAttempt Blocked(AutoUpdateIdleBlocker blocker)
        => new(null, blocker);

    public static AutoUpdateIdleAttempt Accepted(HostMaintenanceLease lease)
        => new(lease, null);
}

/// <summary>阻止闲时自动应用的稳定业务原因，供日志、Web 与 MCP 共享。</summary>
internal sealed record AutoUpdateIdleBlocker(
    string Code,
    string Message,
    string? QueueName,
    DateTime? TriggerTime);

/// <summary>
/// 自动更新维护门禁：先在宿主准入协调域内取得维护租约，再检查调度器状态。
/// Scheduler 的 occurrence 注册也使用同一协调域，因此二次检查与租约转交保持在同一竞态边界内。
/// </summary>
internal sealed class AutoUpdateIdlePolicy
{
    private readonly DispatchCenter _center;
    private readonly Scheduler _scheduler;

    public AutoUpdateIdlePolicy(DispatchCenter center, Scheduler scheduler)
    {
        _center = center;
        _scheduler = scheduler;
    }

    public AutoUpdateIdleAttempt TryAcquire(TimeSpan horizon)
    {
        return _center.WithAdmissionCoordination(() =>
        {
            HostMaintenanceLease? lease = _center.TryAcquireMaintenanceLease(out string reason);
            if (lease is null)
            {
                return AutoUpdateIdleAttempt.Blocked(new AutoUpdateIdleBlocker(
                    "host-busy",
                    string.IsNullOrWhiteSpace(reason) ? "宿主当前繁忙，暂不能应用更新" : reason,
                    null,
                    null));
            }

            try
            {
                AutoUpdateIdleBlocker? blocker = _scheduler.GetAutoUpdateBlocker(horizon);
                if (blocker is not null)
                {
                    lease.Dispose();
                    return AutoUpdateIdleAttempt.Blocked(blocker);
                }
                return AutoUpdateIdleAttempt.Accepted(lease);
            }
            catch (Exception ex)
            {
                lease.Dispose();
                Logger.Warn($"[更新自动化] 闲时检查失败，按繁忙处理：{ex.Message}");
                return AutoUpdateIdleAttempt.Blocked(new AutoUpdateIdleBlocker(
                    "scheduler-check-failed",
                    "调度器状态暂不可用，等待下一次闲时检查",
                    null,
                    null));
            }
        });
    }
}
