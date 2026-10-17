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
    /// <summary>运行用户自写的前置/后置脚本；进程生命周期由 UserHookRunner 负责。</summary>
    internal Task<RunAttemptResult?> RunUserScriptCoreAsync(
        string scriptPath,
        string role,
        RunAttempt attempt,
        CancellationToken token) =>
        _userHookRunner.RunAsync(scriptPath, role, attempt, token);

    internal async Task<RunAttemptResult> RunAttemptCoreAsync(RunAttempt attempt)
    {
        bool providerRun = !string.IsNullOrWhiteSpace(_script.ExecutionProviderId);
        string modeText = _mode == "auto" ? "自动" : "手动";
        var finalizer = new RunAttemptFinalizer(_script, modeText, () => _emulatorDriver);
        RunAttemptResult? budgetError = CheckTotalTimeout();
        if (budgetError is not null)
        {
            return budgetError;
        }
        if (providerRun)
        {
            var descriptor = _executionProviders?.ResolveExecutionProvider(_script.ExecutionProviderId);
            if (descriptor is null || _resolvedSpec?.ProviderPlan is not { } providerPlan || descriptor.PluginVersion != _resolvedSpec.PluginVersion)
                return RunAttemptResult.Fatal("执行 provider 或冻结计划不可用", "provider.unavailable");
            ProviderPlanPolicy.Validate(_script.RootPath, providerPlan);
        }
        if (TaskProtocolRun is not null)
        {
            try { await TaskProtocolRun.BeginAsync(attempt.Number, OperationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return RunAttemptResult.Cancelled("任务发现已取消"); }
            catch (TaskAdmissionBlockedException)
            {
                Logger.Info("[专项任务] 配置检查未通过，未启动脚本或游戏");
                return new RunAttemptResult
                {
                    Status = "blocked",
                    Reason = "配置检查未通过，未启动脚本或游戏",
                    ReasonCode = "tasks.admission_blocked",
                    IsFatal = true,
                };
            }
            catch (Exception ex)
            {
                Logger.Warn($"[专项任务] 配置发现失败：{ex.GetType().Name}");
                return RunAttemptResult.Fatal("无法建立可信任务计划", "tasks.discovery_failed");
            }
        }
        // Attempt 起点日志环境：一次性记录日志格式下所有候选的 path/FileId/length；后续通配符轮换按这张快照决定读取起点。
        var logEnv = new AttemptLogEnvironment(_script, modeText);
        async Task<RunAttemptResult> FinishEarlyAsync(RunAttemptResult early)
        {
            await finalizer.CleanupGameOnEarlyExitAsync(early).ConfigureAwait(false);
            return early;
        }

        ResetGameTrackingForAttempt();

        var gameLauncher = new GameLaunchController(
            _script,
            _resolvedSpec,
            modeText,
            () => OperationToken,
            RemainingRunSeconds,
            () => FindGameProcessId(_gameProcessId),
            processId =>
            {
                _gameProcessId = processId;
                SetPcPreviewTarget(processId);
            },
            () => _emulatorDriver,
            _emulatorSupportProviders,
            driver => _emulatorDriver = driver,
            SetEmulatorPreviewTarget,
            _statusChanged,
            finalizer.TrackGameIdentity);
        RunAttemptResult? gameLaunchError = await gameLauncher.LaunchAsync().ConfigureAwait(false);
        if (gameLaunchError is not null)
        {
            return await FinishEarlyAsync(gameLaunchError).ConfigureAwait(false);
        }

        if (providerRun)
        {
            RunAttemptResult providerResult = await RunProviderAttemptAsync(attempt).ConfigureAwait(false);
            await finalizer.CleanupGameAsync(providerResult, attempt.Number, Math.Max(1, _script.MaxAttempts)).ConfigureAwait(false);
            return providerResult;
        }

        if (!ExecutablePathRules.IsExecutable(_script.MainExe))
        {
            return await FinishEarlyAsync(RunAttemptResult.Failed("脚本主程序路径错误或不是可执行文件")).ConfigureAwait(false);
        }

        string workingDir = string.IsNullOrWhiteSpace(_script.RootPath)
            ? Path.GetDirectoryName(_script.MainExe) ?? ""
            : _script.RootPath;

        (string launchExe, List<string> launchArgs) = SystemActions.ResolveLaunchTarget(_script.MainExe, workingDir, _script.Args);

        ScriptProcessSession processSession;
        bool cleanupConfirmed = true;
        bool killAttempted = false;
        string? excludeGame = EmulatorSupport.IsEmulator(_script)
            ? null
            : (string.IsNullOrWhiteSpace(_script.GameExe) ? null : _script.GameExe);
        bool unownedAutomationRunning = _resolvedSpec?.RootProcessRole == ProcessRole.GameLauncher
            && (_script.PluginType is "oknte" or "okww")
            ? OkRuntimeActivityProbe.Observe(_script.PluginType, _script.RootPath) != "inactive"
            : SystemActions.IsExeRunning(launchExe);
        if (unownedAutomationRunning)
        {
            return await FinishEarlyAsync(RunAttemptResult.Fatal(
                "检测到同路径脚本进程但无法确认属于本次运行，已拒绝重复启动；请自行确认并关闭旧实例",
                "run.unowned_process_running")).ConfigureAwait(false);
        }
        try
        {
            processSession = ScriptProcessSession.Start(
                _script,
                modeText,
                launchExe,
                workingDir,
                launchArgs,
                _statusChanged,
                message => Logger.Info(message),
                _resolvedSpec?.RootProcessRole ?? ProcessRole.AutomationWorker,
                _resolvedSpec?.OutputEncoding ?? "");
            _processOwnership = processSession.Ownership;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            _processOwnership = null;
            return await FinishEarlyAsync(RunAttemptResult.Fatal($"脚本启动失败：目标程序要求管理员权限（{launchExe}）。NexusPipeline 已以管理员身份运行仍被拒绝时，请检查目标程序的权限配置")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _processOwnership = null;
            return await FinishEarlyAsync(RunAttemptResult.Failed($"脚本启动失败：{ex.Message}")).ConfigureAwait(false);
        }

        // 统一游戏窗口前置——无论 LaunchGame 配置（true 由宿主启动、false 由启动器/用户拉起），
        // 只要检测到游戏进程存在即前置其窗口（截图识别需要游戏画面在最前；游戏启动方式复杂由脚本适配，宿主不重复启动）。
        // 模拟器模式跳过（adb 命令行工具，无窗口前置需求）。
        if (!EmulatorSupport.IsEmulator(_script))
        {
            BringGameToFrontIfRunning();
        }

        var attemptMonitor = new AttemptMonitor(_script.PluginType is "maaend" or "maas");
        void OnConsoleData(string? data, LogLevel level)
        {
            if (_token.IsCancellationRequested) return;
            if (data is not null) attemptMonitor.ObserveConsoleLine(data);
            if (string.IsNullOrWhiteSpace(data))
            {
                return;
            }
            if (ShouldPublishConsoleData(_script.LogPath))
            {
                LogLevel observedLevel = LogLevelUtil.ParseObserved(data, level);
                if (_attemptLogLine is not null) _attemptLogLine(data, observedLevel, attempt.Number);
                else _logLine?.Invoke(data, observedLevel);
                if (TaskProtocolRun is not null)
                {
                    TaskProtocolRun.Append("stdout", data + "\n");
                    AppendScriptLog(data);
                }
            }
        }

        processSession.AttachOutput(OnConsoleData);

        bool KillScriptAndConfirm()
        {
            if (killAttempted) return cleanupConfirmed;
            killAttempted = true;
            bool confirmed = processSession.KillAndConfirm(finalizer, excludeGame);
            // Single-instance launchers may route the request to an older GUI;
            // its embedded worker is outside the new Job. Never restore managed
            // configuration while that exact installed runtime is active/unknown.
            if (confirmed && processSession.PreservedLauncher is not null && (_script.PluginType is "oknte" or "okww"))
                confirmed = OkRuntimeActivityProbe.Observe(_script.PluginType, _script.RootPath) == "inactive";
            if (!confirmed)
            {
                cleanupConfirmed = false;
                _configRun?.MarkProcessCleanupUnconfirmed($"脚本「{_script.Name}」进程树清理结果未确认");
            }
            return confirmed;
        }

        // 启动前已存在的残留日志即使被启动后追加写刷新 LastWriteTime，也只从 Attempt 起点长度续读。
        string? resolvedBeforeStart = string.IsNullOrWhiteSpace(_script.LogPath) ? null : LogPattern.ResolveFile(_script.LogPath);
        LogMonitor? monitor = logEnv.CreateMonitor(resolvedBeforeStart);
        var judge = new SessionJudge(_script);
        bool scriptMode = judge.ScriptMode;
        RunAttemptResult? result = null;

        string attemptId = $"{_script.Id}:{attempt.Number}:{attempt.StartTime.Ticks}";

        JudgeSnapshot CaptureJudgeSnapshot(int generation)
        {
            string scriptDir = _configRun?.ScriptDir
                ?? ConfigPaths.ScriptDir(_script.Id, _resolvedUser?.UserKey);
            List<JudgeScriptInputFile> files = JudgeScriptRunner.CollectFiles(_script.ConfigPath, scriptDir)
                .Select(file => new JudgeScriptInputFile
                {
                    Root = file.Root,
                    Path = file.Path,
                    Abs = file.Abs,
                })
                .ToList();
            var (logText, logTruncated) = Results.SnapshotAttemptTail(JudgeScriptRunner.MaxJudgeLogChars);
            ScriptInstance scriptSnapshot = _script.Clone();
            ResolvedScriptUser? userSnapshot = _activeUser is null
                ? null
                : new ResolvedScriptUser(
                    _activeUser.UserId,
                    _activeUser.UserName,
                    _activeUser.Binding.Clone());
            string inputJson = JudgeScriptRunner.BuildInput(
                scriptSnapshot,
                userSnapshot,
                files,
                scriptDir,
                logText,
                logTruncated,
                _screenshotStore.MetadataForAttempt(attempt.Number));
            return new JudgeSnapshot(
                attemptId,
                attempt.Number,
                generation,
                logText,
                DateTime.Now,
                scriptSnapshot,
                userSnapshot,
                scriptDir,
                inputJson,
                files);
        }

        // Judge/配置同步单飞 worker 与最终判定请求收敛在 RuntimeWorkers；终局状态转移收敛在 AttemptTerminator。
        var workers = new RuntimeWorkers(
            attemptId,
            attempt.Number,
            OperationToken,
            modeText,
            _script.Name,
            judge,
            status => _statusChanged?.Invoke(status),
            CaptureJudgeSnapshot,
            replace => _pendingReplaceConfigs = replace,
            request =>
            {
                OperationToken.ThrowIfCancellationRequested();
                _configRun?.SyncToStore(request.FirstCheck);
                OperationToken.ThrowIfCancellationRequested();
            },
             (attemptNumber, trigger, captureToken) => _screenshotStore.CaptureAsync(attemptNumber, trigger, captureToken),
             _http,
             TaskProtocolRun is null ? null : TaskProtocolRun.ObserveAsync);
        var terminator = new AttemptTerminator(workers, judge, status => _statusChanged?.Invoke(status));
        await using var workersScope = workers;

        AttemptMonitorLoopResult monitorLoop = await new AttemptMonitorLoop().RunAsync(
            this,
            attempt,
            modeText,
            attemptId,
            processSession,
            launchExe,
            excludeGame,
            logEnv,
            monitor,
            attemptMonitor,
            judge,
            scriptMode,
            workers,
            terminator,
            _screenshotStore,
            () => OperationToken,
            () => _budgetExpired || _budget?.IsExpired == true,
            KillScriptAndConfirm,
            BringGameToFrontIfRunning,
            ScheduleRecentPcScreenshot,
            NeedsRecentPcScreenshotCache(_script, _resolvedSpec)).ConfigureAwait(false);
        result = monitorLoop.Result;
        monitor = monitorLoop.Monitor;

        // Issue the owned-process stop before waiting for a potentially busy Judge or
        // config worker. Recovery still waits until the workers have truly exited.
        if (_token.IsCancellationRequested)
            CancellationMilestoneChanged?.Invoke(CancellationMilestone.StopIssued, null);
        bool stopped = KillScriptAndConfirm();
        if (_token.IsCancellationRequested && stopped)
            CancellationMilestoneChanged?.Invoke(CancellationMilestone.OwnedProcessesExited, null);
        await workers.StopAsync().ConfigureAwait(false);
        if (_token.IsCancellationRequested)
            CancellationMilestoneChanged?.Invoke(CancellationMilestone.WorkersQuiesced, null);

        monitor?.Dispose();
        monitor = null;

        if (!cleanupConfirmed)
        {
            _pendingReplaceConfigs = null;
            result = RunAttemptResult.Fatal("脚本进程清理未确认，已保留配置现场并阻断后续操作");
        }

        // （P6）：配置替换延迟到杀进程确认退出后应用（此前判断脚本触发时进程可能仍在运行，
        // 复制覆盖 config 存在文件占用/半写窗口）；仅本次尝试失败时应用，重试循环将使用新配置。
        if (cleanupConfirmed && _pendingReplaceConfigs is not null && _pendingReplaceConfigs.Count > 0 && result?.Status == "failed")
        {
            Logger.Info($"[{modeText}运行] 脚本「{_script.Name}」应用判断脚本替换配置（{_pendingReplaceConfigs.Count} 个文件），重试将使用新配置。");
            _configRun?.ApplyReplacements(_pendingReplaceConfigs);
        }
        _pendingReplaceConfigs = null;

        RunAttemptResult finalResult = result ?? RunAttemptResult.Failed("未知原因：未能取得运行结果");
        finalResult.OutputIncomplete |= processSession.OutputIncomplete;
        // 运行收尾后释放进程句柄与 owned Job Object。
        processSession.Dispose();
        try
        {
            await finalizer.CleanupGameAsync(finalResult, attempt.Number, Math.Max(1, _script.MaxAttempts)).ConfigureAwait(false);
        }
        finally
        {
            _processOwnership = null;
        }
        return finalResult;
    }
}
