using System.Diagnostics;
using NexusPipeline.Models;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>
/// attempt 收尾基础设施：脚本进程树和游戏/模拟器资源清理。
/// RunSession 只决定何时调用以及何时应用配置替换。
/// </summary>
internal sealed class RunAttemptFinalizer
{
    private readonly ScriptInstance _script;
    private readonly string _modeText;
    private readonly Func<IEmulatorDriver?> _emulatorDriver;

    public RunAttemptFinalizer(ScriptInstance script, string modeText, Func<IEmulatorDriver?> emulatorDriver)
    {
        _script = script;
        _modeText = modeText;
        _emulatorDriver = emulatorDriver;
    }

    public static bool ShouldCloseGame(RunAttemptResult result, bool forceCloseGame)
    {
        return result.Status == "failed"
            || (result.Status == "cancelled" && forceCloseGame);
    }

    public bool KillScript(Process? process, string launchExe, string? excludeGame, ProcessOwnership? ownership = null)
    {
        if (process is null)
        {
            return true;
        }
        // Job Object 优先提供 launcher 已退出后的 owned child；无 Job 时回退到 root PID + 稳定身份窗口。
        return SystemActions.KillOwnedProcessTree(ownership, process.Id, launchExe, "脚本", excludeProcessBaseName: excludeGame);
    }

    public async Task CleanupGameAsync(RunAttemptResult result, int attemptNumber, int maxAttempts)
    {
        using CancellationTokenSource cleanupCts = CreateCleanupCancellation();
        CancellationToken cleanupToken = cleanupCts.Token;
        try
        {
            string resultStatus = result.Status;
            if (EmulatorSupport.IsEmulator(_script))
            {
                IEmulatorDriver? driver = _emulatorDriver();
                if (driver is null)
                {
                    Logger.Warn($"[{_modeText}运行] 脚本「{_script.Name}」未建立模拟器驱动，跳过模拟器收尾处理。");
                }
                else
                {
                    IEmulatorDriver activeDriver = driver;
                    if (resultStatus == "failed" || (resultStatus == "cancelled" && _script.ForceCloseGame))
                    {
                        Logger.Info($"[{_modeText}运行] 脚本「{_script.Name}」{(resultStatus == "failed" ? "任务失败" : "任务取消且启用强制关闭")}，关闭本次目标应用。");
                        EmulatorCommandResult stop = await activeDriver.StopAppAsync(
                            EmulatorSupport.ParseAmStartPackage(_script.GameArgs),
                            cleanupToken,
                            30).ConfigureAwait(false);
                        if (!stop.Ok)
                        {
                            Logger.Warn($"[{_modeText}运行] 关闭模拟器目标应用失败：{stop.Output.Trim()}");
                        }
                    }
                    bool runEnded = resultStatus is "success" or "partial" or "cancelled"
                        || result.IsFatal
                        || attemptNumber >= Math.Max(1, maxAttempts);
                    if (_script.ForceCloseGame && runEnded && !string.IsNullOrWhiteSpace(_script.GameExe))
                    {
                        Logger.Info($"[{_modeText}运行] 脚本「{_script.Name}」运行结束，关闭模拟器。");
                        EmulatorCommandResult shutdown = await activeDriver.ShutdownAsync(cleanupToken, 30).ConfigureAwait(false);
                        if (shutdown.Ok)
                        {
                            Logger.Info($"[{_modeText}运行] 脚本「{_script.Name}」{shutdown.Output}。");
                        }
                        else
                        {
                            Logger.Warn($"[{_modeText}运行] 脚本「{_script.Name}」{shutdown.Output}");
                        }
                    }
                }
            }
            else if (resultStatus == "failed")
            {
                if (!string.IsNullOrWhiteSpace(_script.GameExe))
                {
                    Logger.Info($"[{_modeText}运行] 脚本「{_script.Name}」任务失败，强制结束游戏进程。");
                    SystemActions.KillByName(_script.GameExe, "游戏");
                }
            }
            else if (_script.ForceCloseGame && !string.IsNullOrWhiteSpace(_script.GameExe))
            {
                SystemActions.KillByName(_script.GameExe, "游戏");
            }
            Logger.Info($"[{_modeText}运行] 脚本「{_script.Name}」本次尝试清理完成。");
        }
        catch (OperationCanceledException) when (cleanupCts.IsCancellationRequested)
        {
            Logger.Warn($"[{_modeText}运行] 脚本「{_script.Name}」清理达到独立截止时间，已结束清理阶段。");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[{_modeText}运行] 脚本「{_script.Name}」清理失败：{ex.Message}");
        }
    }

    public async Task CleanupGameOnEarlyExitAsync(RunAttemptResult early)
    {
        if (!ShouldCloseGame(early, _script.ForceCloseGame) || string.IsNullOrWhiteSpace(_script.GameExe))
        {
            return;
        }
        using CancellationTokenSource cleanupCts = CreateCleanupCancellation();
        try
        {
            if (EmulatorSupport.IsEmulator(_script))
            {
                IEmulatorDriver? driver = _emulatorDriver();
                if (driver is not null)
                {
                    EmulatorCommandResult stop = await driver.StopAppAsync(
                        EmulatorSupport.ParseAmStartPackage(_script.GameArgs),
                        cleanupCts.Token,
                        30).ConfigureAwait(false);
                    if (!stop.Ok)
                    {
                        Logger.Warn($"[警告] 运行提前结束时关闭模拟器目标应用失败：{stop.Output.Trim()}");
                    }
                }
            }
            else
            {
                SystemActions.KillByName(_script.GameExe, "游戏");
            }
        }
        catch (OperationCanceledException) when (cleanupCts.IsCancellationRequested)
        {
            Logger.Warn($"[警告] 运行提前结束时清理游戏达到独立截止时间。");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 运行提前结束时清理游戏失败：{ex.Message}");
        }
    }

    private static CancellationTokenSource CreateCleanupCancellation()
    {
        var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(TestHooks.ScaledSeconds(120)));
        return cts;
    }
}
