using System.Diagnostics;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Modules.Execution.Monitoring;
using NexusPipeline.Modules.Execution.Runtime;
using NexusPipeline.Modules.Execution.Targets;
using NexusPipeline.Modules.History;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Scripts.Validation;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Windows;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Modules.Configuration.Paths;

namespace NexusPipeline.Modules.Execution;

internal sealed partial class ExecutionCoordinator : RunSession
{
    private void SetInitialPreviewTarget()
    {
        bool configured = !string.IsNullOrWhiteSpace(_script.GameExe);
        ExecutionPreviewSource source = !configured
            ? ExecutionPreviewSource.None
            : EmulatorSupport.IsEmulator(_script) ? ExecutionPreviewSource.Emulator : ExecutionPreviewSource.Pc;
        var target = new ExecutionPreviewTarget(
            _script.Id,
            _script.Name,
            source,
            configured ? ExecutionPreviewState.Waiting : ExecutionPreviewState.Unavailable,
            Error: configured ? null : "未配置游戏目标");
        _currentPreviewTarget = target;
        _previewTargetChanged?.Invoke(target);
    }

    private void SetPcPreviewTarget(int? processId)
    {
        var target = new ExecutionPreviewTarget(
            _script.Id,
            _script.Name,
            ExecutionPreviewSource.Pc,
            processId is > 0 ? ExecutionPreviewState.Ready : ExecutionPreviewState.Waiting,
            processId);
        _currentPreviewTarget = target;
        _previewTargetChanged?.Invoke(target);
    }

    private void SetEmulatorPreviewTarget(IEmulatorDriver? driver, bool ready)
    {
        var target = new ExecutionPreviewTarget(
            _script.Id,
            _script.Name,
            ExecutionPreviewSource.Emulator,
            ready ? ExecutionPreviewState.Ready : ExecutionPreviewState.Waiting,
            EmulatorDriver: driver);
        _currentPreviewTarget = target;
        _previewTargetChanged?.Invoke(target);
    }

    internal static int? SelectGameProcessId(
        IEnumerable<int> configuredProcessIds,
        int? preferredProcessId,
        Func<int, bool> isVisible)
    {
        ArgumentNullException.ThrowIfNull(configuredProcessIds);
        ArgumentNullException.ThrowIfNull(isVisible);

        int[] candidates = configuredProcessIds
            .Where(processId => processId > 0)
            .Distinct()
            .ToArray();
        if (preferredProcessId is int preferred
            && preferred > 0
            && candidates.Contains(preferred)
            && isVisible(preferred))
        {
            return preferred;
        }

        foreach (int processId in candidates)
        {
            if (isVisible(processId))
            {
                return processId;
            }
        }

        // 只有按用户配置的进程名没有可见窗口时，才回退到启动关联 PID。
        if (preferredProcessId is int fallback
            && fallback > 0
            && isVisible(fallback))
        {
            return fallback;
        }

        return null;
    }

    private IReadOnlyList<int> FindConfiguredGameProcessIds(
        string processName,
        AttemptProcessSnapshot? processSnapshot)
    {
        if (processSnapshot is not null)
        {
            return processSnapshot.FindProcessIds(processName)
                .Where(processId => processId > 0)
                .Where(MatchesConfiguredGameImage)
                .Distinct()
                .ToArray();
        }

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch
        {
            return Array.Empty<int>();
        }

        try
        {
            return processes
                .Select(process => process.Id)
                .Where(processId => processId > 0)
                .Where(MatchesConfiguredGameImage)
                .Distinct()
                .ToArray();
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    private bool MatchesConfiguredGameImage(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return ProcessIdentity.Capture(process) is { } identity && Path.IsPathFullyQualified(identity.ImageName)
                && ProcessTree.IsSameProcessName(identity.ImageName, _script.GameExe);
        }
        catch { return false; }
    }

    private int? FindGameProcessId(int? preferredProcessId, AttemptProcessSnapshot? processSnapshot = null)
    {
        string processName = Path.GetFileNameWithoutExtension(_script.GameExe ?? "");
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        IReadOnlyList<int> configuredProcessIds = FindConfiguredGameProcessIds(processName, processSnapshot);
        if (preferredProcessId is { } preferred && !MatchesConfiguredGameImage(preferred)) preferredProcessId = null;
        return SelectGameProcessId(
            configuredProcessIds,
            preferredProcessId,
            processId => SystemActions.FindVisibleWindow(processId) != IntPtr.Zero);
    }

    /// <summary>为窗口前置选择配置进程名对应的 PID，即使窗口尚未创建也交给前置轮询等待。</summary>
    private int? FindGameProcessIdForFronting(
        int? preferredProcessId,
        AttemptProcessSnapshot? processSnapshot = null)
    {
        string processName = Path.GetFileNameWithoutExtension(_script.GameExe ?? "");
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        IReadOnlyList<int> configuredProcessIds = FindConfiguredGameProcessIds(processName, processSnapshot);
        if (configuredProcessIds.Count > 0)
        {
            if (preferredProcessId is int preferred
                && configuredProcessIds.Contains(preferred))
            {
                return preferred;
            }

            return configuredProcessIds[0];
        }

        return FindGameProcessId(preferredProcessId, processSnapshot);
    }

    private int? ResolveCurrentPcProcessId()
    {
        int? processId = FindGameProcessId(_gameProcessId);
        if (processId is int pid && pid > 0)
        {
            _gameProcessId = pid;
            if (_currentPreviewTarget?.Source == ExecutionPreviewSource.Pc
                && _currentPreviewTarget.ProcessId != pid)
            {
                SetPcPreviewTarget(pid);
            }
        }

        return processId;
    }

    private void ResetGameTrackingForAttempt()
    {
        _gameProcessId = null;
        lock (_gameFrontSync)
        {
            _gameFronted = false;
            _frontedGameProcessId = null;
            _frontingGameProcessId = null;
            _frontingGameTask = null;
        }

        if (!EmulatorSupport.IsEmulator(_script)
            && !string.IsNullOrWhiteSpace(_script.GameExe))
        {
            SetPcPreviewTarget(null);
        }
    }

    /// <summary>统一游戏窗口前置（轮询检测）：无论 LaunchGame 配置，检测到游戏进程（GameExe 按名）
    /// 存在即后台前置其可见主窗口。游戏由启动器延迟拉起时启动瞬间检测不到——监控循环每轮调用本方法，
    /// 游戏出现即前置（复用 BringToFront 30 秒窗口覆盖「进程出现但窗口未建」），前置一次后由 _gameFronted 停止重复。
    /// 游戏启动方式复杂（启动器常驻/必须以启动器启动等）由脚本专门适配，宿主不重复启动游戏；此处仅做窗口前置。
    /// 找不到窗口（游戏未启动/无窗口）由 BringToFront 内部静默跳过。</summary>
    private void BringGameToFrontIfRunning(AttemptProcessSnapshot? processSnapshot = null)
    {
        if (string.IsNullOrWhiteSpace(_script.GameExe))
        {
            return;
        }
        try
        {
            int? processId = FindGameProcessIdForFronting(_gameProcessId, processSnapshot);
            if (processId is not int pid || pid <= 0)
            {
                return;
            }

            if (_gameProcessId != pid)
            {
                _gameProcessId = pid;
                SetPcPreviewTarget(pid);
            }

            lock (_gameFrontSync)
            {
                if (_frontedGameProcessId == pid)
                {
                    _gameFronted = true;
                    return;
                }
                if (_frontingGameProcessId == pid
                    && _frontingGameTask is { IsCompleted: false })
                {
                    return;
                }

                Task<bool> frontingTask = SystemActions.BringToFrontAsync(pid, "游戏", OperationToken);
                _frontingGameProcessId = pid;
                _frontingGameTask = frontingTask;
                _gameFronted = false;
                _ = ObserveGameFrontAsync(pid, frontingTask);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 检测游戏进程失败：{ex.Message}");
        }
    }

    private async Task ObserveGameFrontAsync(int processId, Task<bool> frontingTask)
    {
        bool succeeded = false;
        try
        {
            succeeded = await frontingTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Attempt 收尾或运行取消时，前置任务自然结束；下一次尝试会重新初始化状态。
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 前置游戏窗口失败：{ex.Message}");
        }

        lock (_gameFrontSync)
        {
            if (_frontingGameProcessId != processId
                || !ReferenceEquals(_frontingGameTask, frontingTask))
            {
                return;
            }

            _frontingGameProcessId = null;
            _frontingGameTask = null;
            _frontedGameProcessId = succeeded ? processId : null;
            _gameFronted = succeeded;
        }
    }

    private void ScheduleRecentPcScreenshot(int attemptNumber)
    {
        ExecutionPreviewTarget? target = _currentPreviewTarget;
        int? processId = target?.Source == ExecutionPreviewSource.Pc
            ? ResolveCurrentPcProcessId() ?? target.ProcessId ?? _gameProcessId
            : null;
        if (processId is not int pid || pid <= 0)
        {
            return;
        }

        _ = _screenshotCapture.TryRefreshPcAsync(attemptNumber, pid, OperationToken);
    }
}
