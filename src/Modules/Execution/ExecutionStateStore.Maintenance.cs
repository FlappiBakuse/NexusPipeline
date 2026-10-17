using NexusPipeline.Modules.Configuration.Contracts;
using NexusPipeline.Modules.Configuration.Recovery;

namespace NexusPipeline.Modules.Execution;

internal sealed partial class ExecutionStateStore
{
    /// <summary>
    /// 原子取得宿主维护租约：冻结新的执行、配置编辑和系统动作准入，并确认当前协调域为空闲。
    /// 租约释放前，任何新的执行或编辑请求都会收到 HostMaintenance。
    /// </summary>
    public HostMaintenanceLease? TryAcquireMaintenanceLease(out string reason)
    {
        lock (_coordinationSync)
        {
            lock (_sync)
            {
                if (_maintenanceActive || _groupState == ExecutionGroupState.Maintenance)
                {
                    reason = "宿主正在进行维护操作";
                    return null;
                }
                if (UnownedRecoveryIsolationLocked().Count > 0)
                {
                    reason = "存在未解决的配置恢复隔离，暂不能维护宿主";
                    return null;
                }
                if (_active.Count > 0)
                {
                    reason = "存在活动运行任务，请先等待完成或取消任务";
                    return null;
                }
                if (_editSessionLeases.Count > 0)
                {
                    reason = "存在配置编辑会话，请先完成或取消编辑";
                    return null;
                }
                if (_pendingSystemAction is not null || _completionIntents.Count > 0)
                {
                    reason = "存在待执行的系统操作，请先完成或取消该操作";
                    return null;
                }
                if (_groupState != ExecutionGroupState.Open)
                {
                    reason = "当前运行组正在收尾，请稍后重试";
                    return null;
                }

                _maintenanceActive = true;
                _groupState = ExecutionGroupState.Maintenance;
                reason = "";
                return new HostMaintenanceLease(ReleaseMaintenanceLease);
            }
        }
    }

    private void ReleaseMaintenanceLease()
    {
        lock (_coordinationSync)
        {
            lock (_sync)
            {
                if (!_maintenanceActive)
                {
                    return;
                }
                _maintenanceActive = false;
                if (_groupState == ExecutionGroupState.Maintenance)
                {
                    _groupState = ExecutionGroupState.Open;
                }
            }
        }
    }
}

/// <summary>宿主维护租约。处置租约后重新开放执行与编辑准入。</summary>
internal sealed class HostMaintenanceLease : IDisposable
{
    private Action? _release;

    internal HostMaintenanceLease(Action? release = null)
    {
        _release = release;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
