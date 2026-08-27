using NexusPipeline.Services.Execution;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>统一的宿主重启请求结果；控制面适配层据此映射各自协议状态。</summary>
internal sealed record RestartRequestResult(bool Accepted, string Code, string Message)
{
    public static RestartRequestResult Success() =>
        new(true, "ok", "服务重启请求已提交");

    public static RestartRequestResult Failure(string code, string message) =>
        new(false, code, message);
}

/// <summary>
/// 宿主重启生命周期协调器。
/// 维护租约在接受请求时原子取得，并一直持有到子进程拉起且当前进程退出；
/// 这样重启等待期间新的运行和配置编辑都会立即收到宿主维护错误。
/// </summary>
internal sealed class HostRestartCoordinator
{
    private readonly Func<(HostMaintenanceLease? Lease, string? Reason)> _acquireMaintenance;

    private readonly Func<bool> _launchChild;

    private readonly Func<bool> _requestExit;

    private readonly Action<Action> _schedule;

    private readonly Action<TimeSpan> _delay;

    private readonly TimeSpan _launchDelay;

    public HostRestartCoordinator(
        Func<(HostMaintenanceLease? Lease, string? Reason)> acquireMaintenance,
        Func<bool> launchChild,
        Func<bool> requestExit,
        Action<Action>? schedule = null,
        Action<TimeSpan>? delay = null,
        TimeSpan? launchDelay = null)
    {
        _acquireMaintenance = acquireMaintenance;
        _launchChild = launchChild;
        _requestExit = requestExit;
        _schedule = schedule ?? (work => _ = Task.Run(work));
        _delay = delay ?? (duration => Thread.Sleep(duration));
        _launchDelay = launchDelay ?? TimeSpan.FromSeconds(1);
    }

    public RestartRequestResult Request(string auditSource, int newPort)
    {
        HostMaintenanceLease? lease = null;
        try
        {
            (lease, string? reason) = _acquireMaintenance();
            if (lease is null)
            {
                return RestartRequestResult.Failure(
                    "service_busy",
                    string.IsNullOrWhiteSpace(reason) ? "服务当前不满足安全重启条件" : reason);
            }

            Audit.Log(auditSource, "重启服务", $"端口 {newPort}");
            HostMaintenanceLease acceptedLease = lease;
            _schedule(() => RunRestart(acceptedLease));
            return RestartRequestResult.Success();
        }
        catch (Exception ex)
        {
            lease?.Dispose();
            Logger.Error($"[重启] 提交重启任务失败：{ex.Message}");
            return RestartRequestResult.Failure("service_busy", "服务当前无法提交重启请求");
        }
    }

    private void RunRestart(HostMaintenanceLease lease)
    {
        bool childLaunched = false;
        try
        {
            _delay(_launchDelay);
            childLaunched = _launchChild();
            if (!childLaunched)
            {
                Logger.Error("[重启] 无法拉起新进程，已释放宿主维护租约。");
                return;
            }

            Logger.Info("[重启] 已拉起新进程，即将退出当前进程。");
            if (!_requestExit())
            {
                // 子进程已经接管启动流程时，继续持有租约，避免旧进程在退出延迟期间接受新任务。
                Logger.Warn("[重启] 当前进程退出请求被延后；宿主维护租约继续有效。");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[重启] 重启生命周期失败：{ex.Message}");
        }
        finally
        {
            // 新进程未成功拉起时，旧进程仍是唯一服务，必须恢复正常准入；
            // 子进程已拉起后租约随旧进程退出释放，继续保留更安全。
            if (!childLaunched)
            {
                lease.Dispose();
            }
        }
    }
}
