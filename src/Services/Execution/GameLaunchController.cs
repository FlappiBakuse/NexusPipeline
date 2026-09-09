using System.Diagnostics;
using NexusPipeline.Models;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Execution;

/// <summary>一次 Attempt 的 PC/模拟器启动控制器；只负责启动与就绪确认，进程收尾仍由 RunAttemptFinalizer 负责。</summary>
internal sealed class GameLaunchController
{
    private readonly ScriptInstance _script;
    private readonly ResolvedScriptSpec? _resolvedSpec;
    private readonly string _modeText;
    private readonly Func<CancellationToken> _operationToken;
    private readonly Func<double> _remainingRunSeconds;
    private readonly Func<int?> _findGameProcessId;
    private readonly Action<int?> _setPcProcessId;
    private readonly Func<IEmulatorDriver?> _getEmulatorDriver;
    private readonly Action<IEmulatorDriver> _setEmulatorDriver;
    private readonly Action<IEmulatorDriver?, bool> _setEmulatorPreviewTarget;
    private readonly Action<string>? _statusChanged;

    public GameLaunchController(
        ScriptInstance script,
        ResolvedScriptSpec? resolvedSpec,
        string modeText,
        Func<CancellationToken> operationToken,
        Func<double> remainingRunSeconds,
        Func<int?> findGameProcessId,
        Action<int?> setPcProcessId,
        Func<IEmulatorDriver?> getEmulatorDriver,
        Action<IEmulatorDriver> setEmulatorDriver,
        Action<IEmulatorDriver?, bool> setEmulatorPreviewTarget,
        Action<string>? statusChanged)
    {
        _script = script;
        _resolvedSpec = resolvedSpec;
        _modeText = modeText;
        _operationToken = operationToken;
        _remainingRunSeconds = remainingRunSeconds;
        _findGameProcessId = findGameProcessId;
        _setPcProcessId = setPcProcessId;
        _getEmulatorDriver = getEmulatorDriver;
        _setEmulatorDriver = setEmulatorDriver;
        _setEmulatorPreviewTarget = setEmulatorPreviewTarget;
        _statusChanged = statusChanged;
    }

    private CancellationToken OperationToken => _operationToken();

    public async Task<RunAttemptResult?> LaunchAsync()
    {
        if (!ExecutionCoordinator.ShouldHostLaunchGame(_script, _resolvedSpec))
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(_script.GameExe))
        {
            Logger.Info($"[{_modeText}运行] 脚本「{_script.Name}」未填写游戏路径，跳过游戏启动。");
            return null;
        }
        if (EmulatorSupport.IsEmulator(_script))
        {
            return await LaunchEmulatorAsync().ConfigureAwait(false);
        }
        if (!TextRules.IsExecutable(_script.GameExe))
        {
            return RunAttemptResult.Failed("游戏路径错误或不是可执行文件");
        }

        _statusChanged?.Invoke("正在启动游戏...");
        try
        {
            string gameWork = Path.GetDirectoryName(_script.GameExe) ?? "";
            bool commandFile = SystemActions.IsCommandFile(_script.GameExe);
            ProcessStartInfo gamePsi = SystemActions.BuildScriptStartInfo(
                _script.GameExe,
                gameWork,
                TextRules.SplitArgs(_script.GameArgs),
                noWindow: false,
                redirect: commandFile);
            if (!commandFile)
            {
                gamePsi.UseShellExecute = true;
            }
            Process? gameProcess = SystemActions.StartWithOutputDrain(gamePsi, disposeWhenExited: true);
            int gamePid = gameProcess?.Id ?? 0;
            if (gamePid > 0)
            {
                _setPcProcessId(gamePid);
                SystemActions.BringToFrontFireAndForget(gamePid, "游戏");
            }
            Logger.Info($"游戏已启动：{_script.GameExe}（等待 {_script.GameWaitSeconds} 秒确认）。");
        }
        catch (Exception ex)
        {
            return RunAttemptResult.Failed($"游戏启动失败：{ex.Message}");
        }

        double remainingSeconds = _remainingRunSeconds();
        if (remainingSeconds <= 0)
        {
            return RunAttemptResult.Fatal($"运行总时间超过限制（{_script.TotalTimeoutMinutes} 分钟）");
        }
        double requestedGameWait = TestHooks.ScaledSeconds(Math.Max(0, _script.GameWaitSeconds));
        bool gameConfirmed;
        try
        {
            gameConfirmed = await WaitForGameProcessAsync(
                TimeSpan.FromSeconds(Math.Min(requestedGameWait, remainingSeconds))).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return RunAttemptResult.Cancelled("已取消（等待游戏启动期间）");
        }
        if (_remainingRunSeconds() <= 0)
        {
            return RunAttemptResult.Fatal($"运行总时间超过限制（{_script.TotalTimeoutMinutes} 分钟）");
        }
        if (!gameConfirmed)
        {
            return RunAttemptResult.Failed($"等待 {_script.GameWaitSeconds} 秒后仍未检测到游戏进程，游戏可能启动失败");
        }
        _setPcProcessId(_findGameProcessId());
        _statusChanged?.Invoke("已确认游戏进程启动");
        Logger.Info($"[{_modeText}运行] 脚本「{_script.Name}」已确认游戏进程启动，继续运行脚本。");
        return null;
    }

    private async Task<bool> WaitForGameProcessAsync(TimeSpan timeout)
    {
        if (SystemActions.IsCommandFile(_script.GameExe))
        {
            await Task.Delay(timeout, OperationToken).ConfigureAwait(false);
            return true;
        }
        DateTime deadline = DateTime.Now + timeout;
        while (true)
        {
            if (SystemActions.IsExeRunning(_script.GameExe))
            {
                return true;
            }
            if (DateTime.Now >= deadline)
            {
                return false;
            }
            await Task.Delay(TestHooks.ScaledMs(1000), OperationToken).ConfigureAwait(false);
        }
    }

    private async Task<RunAttemptResult?> LaunchEmulatorAsync()
    {
        try
        {
            return await LaunchEmulatorCoreAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return RunAttemptResult.Cancelled("已取消（启动模拟器应用期间）");
        }
    }

    private async Task<RunAttemptResult?> LaunchEmulatorCoreAsync()
    {
        if (!EmulatorSupport.IsValidAdbAddress(_script.GameExe))
        {
            return RunAttemptResult.Failed($"模拟器ADB地址格式不正确（应为 主机:端口，如 127.0.0.1:16384）：{_script.GameExe}");
        }
        string[] startArgs = TextRules.SplitArgs(_script.GameArgs).ToArray();
        if (startArgs.Length == 0)
        {
            return RunAttemptResult.Failed("模拟器模式未填写启动参数（am start 参数，如 -n 包名/Activity）");
        }

        IEmulatorDriver? driver = _getEmulatorDriver();
        if (driver is null)
        {
            EmulatorTarget target = await EmulatorDetector.DetectAsync(
                _script.GameExe,
                OperationToken,
                RemainingCommandSeconds(30)).ConfigureAwait(false);
            if (target.Kind == EmulatorKind.DetectionError)
            {
                return RunAttemptResult.Failed(target.DetectionError ?? "模拟器目标识别失败");
            }
            driver = EmulatorDriverFactory.Create(target);
            _setEmulatorDriver(driver);
            _setEmulatorPreviewTarget(driver, false);
            Logger.Info($"[{_modeText}运行] 脚本「{_script.Name}」已冻结模拟器驱动：{driver.Kind}（目标 {_script.GameExe}）。");
        }

        _statusChanged?.Invoke("正在连接模拟器...");
        if (_remainingRunSeconds() <= 0)
        {
            return RunAttemptResult.Fatal($"运行总时间超过限制（{_script.TotalTimeoutMinutes} 分钟）");
        }
        EmulatorCommandResult ready = await driver.EnsureReadyAsync(
            OperationToken,
            RemainingCommandSeconds(30)).ConfigureAwait(false);
        if (_remainingRunSeconds() <= 0)
        {
            return RunAttemptResult.Fatal($"运行总时间超过限制（{_script.TotalTimeoutMinutes} 分钟）");
        }
        if (!ready.Ok)
        {
            return RunAttemptResult.Failed($"模拟器连接/准备失败（{_script.GameExe}）：{ready.Output.Trim()}");
        }

        _statusChanged?.Invoke("正在启动模拟器应用...");
        if (_remainingRunSeconds() <= 0)
        {
            return RunAttemptResult.Fatal($"运行总时间超过限制（{_script.TotalTimeoutMinutes} 分钟）");
        }
        EmulatorCommandResult start = await driver.StartAppAsync(
            startArgs,
            OperationToken,
            RemainingCommandSeconds(30)).ConfigureAwait(false);
        if (_remainingRunSeconds() <= 0)
        {
            return RunAttemptResult.Fatal($"运行总时间超过限制（{_script.TotalTimeoutMinutes} 分钟）");
        }
        if (!start.Ok)
        {
            return RunAttemptResult.Failed($"模拟器应用启动失败：{start.Output.Trim()}");
        }

        Logger.Info($"[{_modeText}运行] 脚本「{_script.Name}」模拟器应用启动命令已执行（{_script.GameExe}，等待 {_script.GameWaitSeconds} 秒确认前台）。");
        string? targetPackage = EmulatorSupport.ParseAmStartPackage(_script.GameArgs);
        bool confirmed = targetPackage is null
            ? true
            : await WaitForEmulatorAppAsync(
                TimeSpan.FromSeconds(Math.Min(
                    TestHooks.ScaledSeconds(Math.Max(0, _script.GameWaitSeconds)),
                    _remainingRunSeconds())),
                targetPackage,
                driver).ConfigureAwait(false);
        if (_remainingRunSeconds() <= 0)
        {
            return RunAttemptResult.Fatal($"运行总时间超过限制（{_script.TotalTimeoutMinutes} 分钟）");
        }
        if (!confirmed)
        {
            return RunAttemptResult.Failed($"等待 {_script.GameWaitSeconds} 秒后模拟器前台未出现应用（{targetPackage}），应用可能启动失败");
        }
        _setEmulatorPreviewTarget(driver, true);
        _statusChanged?.Invoke("已确认模拟器应用启动");
        Logger.Info($"[{_modeText}运行] 脚本「{_script.Name}」已确认模拟器应用启动，继续运行脚本。");
        return null;
    }

    private async Task<bool> WaitForEmulatorAppAsync(
        TimeSpan timeout,
        string targetPackage,
        IEmulatorDriver driver)
    {
        DateTime deadline = DateTime.Now + timeout;
        while (true)
        {
            if (_remainingRunSeconds() <= 0)
            {
                return false;
            }
            string? foreground = await driver.GetForegroundPackageAsync(
                OperationToken,
                RemainingCommandSeconds(30)).ConfigureAwait(false);
            if (string.Equals(foreground, targetPackage, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (DateTime.Now >= deadline)
            {
                return false;
            }
            await Task.Delay(TestHooks.ScaledMs(1000), OperationToken).ConfigureAwait(false);
        }
    }

    private int RemainingCommandSeconds(int cap) =>
        _remainingRunSeconds() is double remaining && !double.IsPositiveInfinity(remaining)
            ? Math.Max(1, Math.Min(cap, (int)Math.Ceiling(remaining)))
            : cap;
}
