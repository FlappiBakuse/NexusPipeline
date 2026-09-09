using System.Diagnostics;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using NexusPipeline.Utilities;
using NexusPipeline.App.Abstractions;

namespace NexusPipeline.Services.Execution;

/// <summary>一次运行的应用层协调器；状态由基类 RunSession 持有。</summary>
internal sealed class ExecutionCoordinator : RunSession
{
    /// <summary>成功判定后等待脚本自行退出的宽限秒数（NEXUS_TIME_SCALE 加速时按比例缩放）。</summary>
    private const int ExitGraceSecondsAfterMarker = 60;

    private readonly IUserRepository _users;

    private readonly ResolvedScriptSpec? _resolvedSpec;

    private readonly Action<ExecutionPreviewTarget>? _previewTargetChanged;

    private readonly RunScreenshotStore _screenshotStore;

    private readonly AttemptScreenshotCapture _screenshotCapture;

    private int? _gameProcessId;

    private ExecutionPreviewTarget? _currentPreviewTarget;

    private IEmulatorDriver? _emulatorDriver;

    private CancellationTokenSource? _budgetExpiryCts;

    private CancellationTokenSource? _operationCts;

    private RunBudgetWatchdog? _budgetWatchdog;

    private readonly UserHookRunner _userHookRunner;

    private volatile bool _budgetExpired;

    private CancellationToken OperationToken => _operationCts?.Token ?? _token;

    public ExecutionCoordinator(ScriptInstance script, string mode, string queueId, string queueName, string? userName, CancellationToken token,
        Action<int, int>? attemptChanged,
        Action<string>? statusChanged,
        Action<string, LogLevel>? logLine,
        Action<ExecutionPreviewTarget>? previewTargetChanged,
        IUserRepository users,
        ResolvedScriptUser? resolvedUser = null,
        ResolvedScriptSpec? resolvedSpec = null)
        : base(script, mode, queueId, queueName, userName, token, resolvedUser, attemptChanged, statusChanged, logLine)
    {
        _users = users;
        _resolvedSpec = resolvedSpec;
        _previewTargetChanged = previewTargetChanged;
        _screenshotCapture = new AttemptScreenshotCapture(
            _script,
            () => _currentPreviewTarget,
            () => _gameProcessId,
            () => _emulatorDriver);
        _screenshotStore = new RunScreenshotStore(_screenshotCapture.CaptureAsync);
        _userHookRunner = new UserHookRunner(
            _script,
            _mode,
            _statusChanged,
            _logLine,
            RemainingRunSeconds,
            () => _budgetExpired || _budget?.IsExpired == true,
            message => _configRun?.MarkProcessCleanupUnconfirmed(message));
        SetInitialPreviewTarget();
    }

    internal RunScreenshotStore ScreenshotStore => _screenshotStore;

    internal void DisposeScreenshots()
    {
        _screenshotStore.Dispose();
        _screenshotCapture.Dispose();
    }

    internal static bool ShouldPublishConsoleData(string? logPath) => string.IsNullOrWhiteSpace(logPath);

    internal static bool ShouldHostLaunchGame(ScriptInstance script, ResolvedScriptSpec? spec)
    {
        return script.LaunchGame
            && !(spec?.SelfManagedPcLaunch == true && !EmulatorSupport.IsEmulator(script));
    }

    public async Task<RunRecord> RunAsync()
    {
        _budget = new RunBudget(_script.TotalTimeoutMinutes, DateTime.Now);
        var record = new RunRecord
        {
            ScriptInstanceId = _script.Id,
            ScriptName = _script.Name,
            QueueId = _queueId,
            QueueName = _queueName,
            Mode = _mode,
            UserName = _userName ?? "",
            UserId = _resolvedUser?.UserId ?? "",
            PluginVersion = _resolvedSpec?.PluginVersion ?? "",
            ProfileHash = _resolvedSpec?.ProfileHash ?? "",
            JudgeSourceKind = _resolvedSpec?.JudgeScript.SourceKind ?? "",
            JudgeHash = _resolvedSpec?.JudgeScript.ContentHash ?? "",
            StartTime = DateTime.Now,
            ResultCode = "run.running",
        };

        ResolvedScriptUser? resolvedUser = _resolvedUser
            ?? (string.IsNullOrWhiteSpace(_userName)
                ? null
                : _users.ResolveEnabledBinding(_script, _userName));
        ResolvedScriptUser? user = resolvedUser;
        if (!string.IsNullOrWhiteSpace(_userName) && user is null)
        {
            record.Status = "failed";
            record.EndTime = DateTime.Now;
            record.ResultDetail = $"用户「{_userName}」不存在或已禁用";
            record.ResultCode = "run.user_unavailable";
            record.ResultArgs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["user"] = _userName,
            };
            return record;
        }
        if (_resolvedUser?.Spec is { Succeeded: false } userSpec)
        {
            // 按用户绑定输入解析的专项快照失败（如绑定的配置文件已被改名或删除）：只影响该用户，
            // 在编辑配置中重新选择接管配置即可恢复。
            record.Status = "failed";
            record.EndTime = DateTime.Now;
            record.ResultDetail = userSpec.Error ?? "脚本配置解析失败";
            record.ResultCode = "run.spec_failed";
            return record;
        }
        if (_resolvedSpec is { ConfigInputCandidates.Count: >= 2 })
        {
            // 接管的配置文件/实例目录尚未选定：目录型 configPath 在未定时会解析为存在的目录，
            // 继续运行会把整个目录采用为用户快照，必须先在编辑配置中选择。
            record.Status = "failed";
            record.EndTime = DateTime.Now;
            record.ResultDetail = "当前脚本目录存在多个配置，请先编辑配置选择要接管的配置";
            record.ResultCode = "run.config_selection_required";
            return record;
        }
        if (user is not null)
        {
            record.UserName = user.UserName;
        }
        _activeUser = user;

        _budgetExpiryCts = new CancellationTokenSource();
        _operationCts = CancellationTokenSource.CreateLinkedTokenSource(_token, _budgetExpiryCts.Token);
        _budgetWatchdog = new RunBudgetWatchdog(_budget, _token, () =>
        {
            _budgetExpired = true;
            try
            {
                _budgetExpiryCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        });
        _budgetWatchdog.Start();

        var retryPolicy = new RetryPolicy(_script.MaxAttempts);
        int maxAttempts = retryPolicy.MaxAttempts;
        record.MaxAttempts = maxAttempts;
        _attemptChanged?.Invoke(1, maxAttempts);

        try
        {
            // 配置运行会话在 try 内创建并准备，任何 Prepare 异常都统一进入幂等 FinalizeRun。
            _configRun = new ConfigRunSession(
                _script.Id,
                resolvedUser?.UserKey,
                _script.ConfigPath,
                _script.HasJudgeScript(),
                _resolvedSpec);
            _configRun.PrepareScriptArea();
            if (user is not null && !string.IsNullOrWhiteSpace(_script.ConfigPath))
            {
                _statusChanged?.Invoke("正在加载用户配置...");
                if (!_configRun.Prepare(out string? prepError))
                {
                    record.Status = "failed";
                    record.EndTime = DateTime.Now;
                    record.ResultDetail = $"用户配置加载失败：{prepError}";
                    record.ResultCode = "run.user_config_load_failed";
                    Logger.Error($"[错误] 脚本「{_script.Name}」用户「{user.UserName}」配置加载失败：{prepError}");
                    return record;
                }
            }

            for (int attemptNo = 1; attemptNo <= maxAttempts; attemptNo++)
            {
                _attemptChanged?.Invoke(attemptNo, maxAttempts);
                if (attemptNo > 1 && _configRun.IsPrepared)
                {
                    string? retryError = _configRun.PrepareForRetry();
                    if (retryError is not null)
                    {
                        _attemptLogStart = _scriptFullLog.Length;
                        var retryAttempt = new RunAttempt
                        {
                            Number = attemptNo,
                            StartTime = DateTime.Now,
                            EndTime = DateTime.Now,
                            Status = "failed",
                            Reason = "重试前配置交换失败：" + retryError,
                            ReasonCode = "run.retry_prepare_failed",
                        };
                        AppendScriptLog($"===== 第 {attemptNo}/{maxAttempts} 次尝试 开始（{retryAttempt.StartTime:HH:mm:ss}） =====");
                        AppendScriptLog($"===== 第 {attemptNo}/{maxAttempts} 次尝试 结束：failed（{retryAttempt.Reason}） =====");
                        record.AttemptDetails.Add(retryAttempt);
                        Results.CompleteAttempt();
                        record.Attempts = attemptNo;
                        record.Status = "failed";
                        record.EndTime = retryAttempt.EndTime;
                        record.ResultDetail = retryAttempt.Reason;
                        record.ResultCode = retryAttempt.ReasonCode;
                        break;
                    }
                }
                var attempt = new RunAttempt
                {
                    Number = attemptNo,
                    StartTime = DateTime.Now,
                };
                record.AttemptDetails.Add(attempt);
                // 段起点设置在「开始」头之前——此前段含「结束」头不含「开始」头（首尾不对称），
                // 判断脚本输入与按尝试分批落盘的日志段现在从「开始」头起算。
                _attemptLogStart = _scriptFullLog.Length;
                AppendScriptLog($"===== 第 {attemptNo}/{maxAttempts} 次尝试 开始（{attempt.StartTime:HH:mm:ss}） =====");
                _screenshotCapture.BeginAttempt(attemptNo);

                Logger.Info($"===== 脚本「{_script.Name}」第 {attemptNo}/{maxAttempts} 次尝试 =====");
                RunAttemptResult result;
                bool runPreRun = AttemptLifecycle.ShouldRunPreRun(
                    user is not null && !string.IsNullOrWhiteSpace(user.Binding.PreRunScript),
                    user?.Binding.PreRunOnceOnly ?? false,
                    _preRunCompletedSuccessfully);
                bool mainExecuted = true;
                if (runPreRun)
                {
                    RunAttemptResult? preResult = await RunUserScriptCoreAsync(user!.Binding.PreRunScript, "任务前", attempt, OperationToken).ConfigureAwait(false);
                    if (preResult is not null)
                    {
                        // PreRun 只有成功（返回 null）才允许进入 Main；失败/取消直接结束本次 Attempt。
                        mainExecuted = false;
                        result = preResult;
                    }
                    else
                    {
                        _preRunCompletedSuccessfully = true;
                        result = await RunAttemptCoreAsync(attempt).ConfigureAwait(false);
                    }
                }
                else
                {
                    result = await RunAttemptCoreAsync(attempt).ConfigureAwait(false);
                }

                if (mainExecuted && result.Status != "cancelled"
                    && user is not null && !string.IsNullOrWhiteSpace(user.Binding.PostRunScript)
                    && AttemptLifecycle.ShouldRunPostRun(
                        user.Binding.PostRunOnFinalOnly,
                        attemptNo,
                        retryPolicy,
                        result))
                {
                    RunAttemptResult? postResult = await RunUserScriptCoreAsync(user!.Binding.PostRunScript, "任务后", attempt, OperationToken).ConfigureAwait(false);
                    if (postResult is not null)
                    {
                        result = RunAttemptResult.MergePostRun(result, postResult);
                    }
                }

                attempt.EndTime = DateTime.Now;
                attempt.Status = result.Status;
                attempt.Reason = result.Reason;
                attempt.ReasonCode = result.ReasonCode;
                attempt.ReasonArgs = new Dictionary<string, string>(result.ReasonArgs, StringComparer.Ordinal);
                record.Attempts = attemptNo;
                record.ResultCode = result.ReasonCode;
                record.ResultArgs = new Dictionary<string, string>(result.ReasonArgs, StringComparer.Ordinal);
                record.NotifyScreenshotId = result.NotifyScreenshotId;
                if (!string.IsNullOrWhiteSpace(result.NotifyText))
                {
                    record.CustomNotifyText = result.NotifyText;
                }
                AppendScriptLog($"===== 第 {attemptNo}/{maxAttempts} 次尝试 结束：{result.Status}（{result.Reason}） =====");
                Logger.Info($"第 {attemptNo} 次尝试结束：{result.Status}（{result.Reason}）");
                Results.CompleteAttempt();

                if (result.Status is "success" or "partial")
                {
                    record.Status = result.Status;
                    record.EndTime = DateTime.Now;
                    record.ResultDetail = result.Status == "partial"
                        ? (string.IsNullOrWhiteSpace(result.Reason) ? "判断脚本判定部分完成" : result.Reason)
                        : attemptNo == 1 ? "一次成功" : $"第 {attemptNo} 次尝试成功";
                    break;
                }
                if (result.IsFatal)
                {
                    record.Status = result.Status;
                    record.EndTime = DateTime.Now;
                    record.ResultDetail = result.Reason;
                    break;
                }
                if (!retryPolicy.ShouldRetry(attemptNo, result))
                {
                    record.Status = "failed";
                    record.EndTime = DateTime.Now;
                    record.ResultDetail = $"达到最大尝试次数（{maxAttempts} 次）仍失败，最后原因：{result.Reason}";
                    record.ResultCode = "run.max_attempts";
                    record.ResultArgs = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["maximum"] = maxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["reason"] = result.Reason,
                    };
                    break;
                }
            }

            return record;
        }
        finally
        {
            if (_budgetWatchdog is not null)
            {
                await _budgetWatchdog.DisposeAsync().ConfigureAwait(false);
                _budgetWatchdog = null;
            }
            _operationCts?.Dispose();
            _operationCts = null;
            _budgetExpiryCts?.Dispose();
            _budgetExpiryCts = null;
            // ConfigRunSession 是唯一的运行收尾入口：自动更新同步 → 插队还原 →
            // 判断脚本目录清理 → 配置交换还原。
            if (_configRun is not null)
            {
                string? restoreError = _configRun.FinalizeRun(_script.AutoUpdateConfig);
                if (restoreError is not null)
                {
                    string msg = $"（警告：配置还原失败，现场已保留，详见日志）";
                    record.ResultDetail += msg;
                    Logger.Error($"[错误] 脚本「{_script.Name}」用户「{user?.UserName ?? _userName ?? ""}」配置还原失败：{restoreError}");
                }
            }
        }
    }

    /// <summary>运行用户自写的前置/后置脚本；进程生命周期由 UserHookRunner 负责。</summary>
    internal Task<RunAttemptResult?> RunUserScriptCoreAsync(
        string scriptPath,
        string role,
        RunAttempt attempt,
        CancellationToken token) =>
        _userHookRunner.RunAsync(scriptPath, role, attempt, token);

    internal async Task<RunAttemptResult> RunAttemptCoreAsync(RunAttempt attempt)
    {
        string modeText = _mode == "auto" ? "自动" : "手动";
        var finalizer = new RunAttemptFinalizer(_script, modeText, () => _emulatorDriver);
        RunAttemptResult? budgetError = CheckTotalTimeout();
        if (budgetError is not null)
        {
            return budgetError;
        }
        // Attempt 起点日志环境：一次性记录日志格式下所有候选的 path/FileId/length；后续通配符轮换按这张快照决定读取起点。
        var logEnv = new AttemptLogEnvironment(_script, modeText);
        async Task<RunAttemptResult> FinishEarlyAsync(RunAttemptResult early)
        {
            await finalizer.CleanupGameOnEarlyExitAsync(early).ConfigureAwait(false);
            return early;
        }

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
            driver => _emulatorDriver = driver,
            SetEmulatorPreviewTarget,
            _statusChanged);
        RunAttemptResult? gameLaunchError = await gameLauncher.LaunchAsync().ConfigureAwait(false);
        if (gameLaunchError is not null)
        {
            return await FinishEarlyAsync(gameLaunchError).ConfigureAwait(false);
        }

        if (!TextRules.IsExecutable(_script.MainExe))
        {
            return await FinishEarlyAsync(RunAttemptResult.Failed("脚本主程序路径错误或不是可执行文件")).ConfigureAwait(false);
        }

        string workingDir = string.IsNullOrWhiteSpace(_script.RootPath)
            ? Path.GetDirectoryName(_script.MainExe) ?? ""
            : _script.RootPath;

        (string launchExe, List<string> launchArgs) = SystemActions.ResolveLaunchTarget(_script.MainExe, workingDir, _script.Args);

        ScriptProcessSession processSession;
        bool cleanupConfirmed = true;
        string? excludeGame = EmulatorSupport.IsEmulator(_script)
            ? null
            : (string.IsNullOrWhiteSpace(_script.GameExe) ? null : Path.GetFileNameWithoutExtension(_script.GameExe));
        if (SystemActions.IsExeRunning(launchExe))
        {
            Logger.Warn($"[{modeText}运行] 脚本「{_script.Name}」检测到旧进程，先结束后重新启动。");
            _statusChanged?.Invoke("检测到旧脚本进程，正在结束后重新启动...");
            if (!SystemActions.KillExistingProcessesByIdentity(launchExe, "旧脚本", excludeProcessBaseName: excludeGame))
            {
                return await FinishEarlyAsync(RunAttemptResult.Fatal("检测到旧脚本进程但无法确认其退出，已拒绝重复启动")).ConfigureAwait(false);
            }
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
                message => Logger.Info(message));
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

        void OnConsoleData(string? data, LogLevel level)
        {
            if (string.IsNullOrWhiteSpace(data))
            {
                return;
            }
            if (ShouldPublishConsoleData(_script.LogPath))
            {
                _logLine?.Invoke(data, LogLevelUtil.ParseObserved(data, level));
            }
        }

        processSession.AttachOutput(OnConsoleData);

        bool KillScriptAndConfirm()
        {
            bool confirmed = processSession.KillAndConfirm(finalizer, excludeGame);
            if (!confirmed)
            {
                cleanupConfirmed = false;
                _configRun?.MarkProcessCleanupUnconfirmed($"脚本「{_script.Name}」进程树清理结果未确认");
            }
            return confirmed;
        }

        // 启动前已存在的残留日志即使被启动后追加写刷新 LastWriteTime，也只从 Attempt 起点长度续读。
        string? resolvedBeforeStart = string.IsNullOrWhiteSpace(_script.LogPath) ? null : LogPattern.ResolveFile(_script.LogPath);
        DateTime attemptStart = DateTime.Now;
        LogMonitor? monitor = logEnv.CreateMonitor(resolvedBeforeStart);
        var attemptMonitor = new AttemptMonitor();
        var judge = new SessionJudge(_script);
        bool scriptMode = judge.ScriptMode;
        RunAttemptResult? result = null;

        string attemptId = $"{_script.Id}:{attempt.Number}:{attempt.StartTime.Ticks}";

        JudgeSnapshot CaptureJudgeSnapshot(int generation)
        {
            string scriptDir = _configRun?.ScriptDir
                ?? UserConfigManager.ScriptDir(_script.Id, _resolvedUser?.UserKey);
            List<JudgeScriptInputFile> files = JudgeScriptRunner.CollectFiles(_script.ConfigPath, scriptDir)
                .Select(file => new JudgeScriptInputFile
                {
                    Root = file.Root,
                    Path = file.Path,
                    Abs = file.Abs,
                })
                .ToList();
            int logLength = Math.Max(0, _scriptFullLog.Length - _attemptLogStart);
            bool logTruncated = logLength > JudgeScriptRunner.MaxJudgeLogChars;
            string logText = logTruncated
                ? _scriptFullLog.ToString(_attemptLogStart + logLength - JudgeScriptRunner.MaxJudgeLogChars, JudgeScriptRunner.MaxJudgeLogChars)
                : _scriptFullLog.ToString(_attemptLogStart, logLength);
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
            (attemptNumber, trigger, captureToken) => _screenshotStore.CaptureAsync(attemptNumber, trigger, captureToken));
        var terminator = new AttemptTerminator(workers, judge, status => _statusChanged?.Invoke(status));
        await using var workersScope = workers;

        AttemptMonitorLoopResult monitorLoop = await new AttemptMonitorLoop().RunAsync(
            this,
            attempt,
            modeText,
            attemptId,
            attemptStart,
            processSession.Process,
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
            ScheduleRecentPcScreenshot).ConfigureAwait(false);
        result = monitorLoop.Result;
        monitor = monitorLoop.Monitor;

        // 先收拢后台 worker，再进入进程清理与 ConfigRunSession.FinalizeRun，
        // 防止旧 Attempt 的 Judge/配置同步在收尾阶段继续写入状态或文件。
        await workers.StopAsync().ConfigureAwait(false);

        monitor?.Dispose();
        monitor = null;

        KillScriptAndConfirm();

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

    private double RemainingRunSeconds()
    {
        if (_script.TotalTimeoutMinutes <= 0)
        {
            return double.PositiveInfinity;
        }
        return _budget?.RemainingSeconds ?? double.PositiveInfinity;
    }

    private RunAttemptResult? CheckTotalTimeout()
    {
        return RemainingRunSeconds() <= 0
            ? RunAttemptResult.Fatal($"运行总时间超过限制（{_script.TotalTimeoutMinutes} 分钟）")
            : null;
    }

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

    private int? FindGameProcessId(int? preferredProcessId)
    {
        if (preferredProcessId is int preferred && preferred > 0
            && SystemActions.FindVisibleWindow(preferred) != IntPtr.Zero)
        {
            return preferred;
        }
        string processName = Path.GetFileNameWithoutExtension(_script.GameExe ?? "");
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch
        {
            return null;
        }
        try
        {
            foreach (Process process in processes)
            {
                if (SystemActions.FindVisibleWindow(process.Id) != IntPtr.Zero)
                {
                    return process.Id;
                }
            }
            return null;
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>统一游戏窗口前置（， 轮询检测）：无论 LaunchGame 配置，检测到游戏进程（GameExe 按名）
    /// 存在即后台前置其可见主窗口。游戏由启动器延迟拉起时启动瞬间检测不到——监控循环每轮调用本方法，
    /// 游戏出现即前置（复用 BringToFront 30 秒窗口覆盖「进程出现但窗口未建」），前置一次后由 _gameFronted 停止重复。
    /// 游戏启动方式复杂（启动器常驻/必须以启动器启动等）由脚本专门适配，宿主不重复启动游戏；此处仅做窗口前置。
    /// 找不到窗口（游戏未启动/无窗口）由 BringToFront 内部静默跳过。</summary>
    private void BringGameToFrontIfRunning()
    {
        if (_gameFronted || string.IsNullOrWhiteSpace(_script.GameExe))
        {
            return;
        }
        try
        {
            int? processId = FindGameProcessId(_gameProcessId);
            if (processId is int pid)
            {
                _gameProcessId = pid;
                SetPcPreviewTarget(pid);
                SystemActions.BringToFrontFireAndForget(pid, "游戏");
                _gameFronted = true;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 检测游戏进程失败：{ex.Message}");
        }
    }

    private void ScheduleRecentPcScreenshot(int attemptNumber)
    {
        ExecutionPreviewTarget? target = _currentPreviewTarget;
        int? processId = target?.Source == ExecutionPreviewSource.Pc
            ? target.ProcessId ?? _gameProcessId
            : null;
        if (processId is not int pid || pid <= 0)
        {
            return;
        }

        _ = _screenshotCapture.TryRefreshPcAsync(attemptNumber, pid, OperationToken);
    }
}
