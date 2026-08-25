using System.Diagnostics;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using NexusPipeline.Utilities;
using NexusPipeline.App.Abstractions;

namespace NexusPipeline.Services.Execution;

/// <summary>一次运行的应用层协调器；状态由基类 RunSession 持有。</summary>
internal sealed class ExecutionCoordinator : RunSession, IAttemptExecutionHost
{
    /// <summary>成功判定后等待脚本自行退出的宽限秒数（NEXUS_TIME_SCALE 加速时按比例缩放）。</summary>
    private const int ExitGraceSecondsAfterMarker = 60;

    private readonly AttemptRunner _attemptRunner;

    private readonly IUserRepository _users;

    private EmulatorTarget? _emulatorTarget;

    private IEmulatorDriver? _emulatorDriver;

    private CancellationTokenSource? _budgetExpiryCts;

    private CancellationTokenSource? _operationCts;

    private RunBudgetWatchdog? _budgetWatchdog;

    private volatile bool _budgetExpired;

    private CancellationToken OperationToken => _operationCts?.Token ?? _token;

    public ExecutionCoordinator(ScriptInstance script, string mode, string queueId, string queueName, string? userName, CancellationToken token,
        Action<int, int>? attemptChanged,
        Action<string>? statusChanged,
        Action<string>? logLine,
        IUserRepository users,
        ResolvedScriptUser? resolvedUser = null)
        : base(script, mode, queueId, queueName, userName, token, resolvedUser, attemptChanged, statusChanged, logLine)
    {
        _users = users;
        _attemptRunner = new AttemptRunner(this);
    }

    Task<RunAttemptResult?> IAttemptExecutionHost.RunUserScriptCoreAsync(string scriptPath, string role, RunAttempt attempt, CancellationToken token)
        => RunUserScriptCoreAsync(scriptPath, role, attempt, token);

    Task<RunAttemptResult> IAttemptExecutionHost.RunAttemptCoreAsync(RunAttempt attempt)
        => RunAttemptCoreAsync(attempt);

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
            StartTime = DateTime.Now,
        };

        ResolvedScriptUser? resolvedUser = _resolvedUser
            ?? (string.IsNullOrWhiteSpace(_userName)
                ? null
                : _users.ResolveEnabledBinding(_script, _userName));
        ScriptUser? user = resolvedUser?.ToLegacyScriptUser();
        if (!string.IsNullOrWhiteSpace(_userName) && user is null)
        {
            record.Status = "failed";
            record.FinalStatus = "failed";
            record.EndTime = DateTime.Now;
            record.ResultDetail = $"用户「{_userName}」不存在或已禁用";
            return record;
        }
        if (user is not null)
        {
            record.UserName = user.Name;
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
                resolvedUser?.UserKey ?? user?.Name,
                resolvedUser?.UserName ?? user?.Name,
                _script.ConfigPath,
                _script.HasJudgeScript());
            _configRun.PrepareScriptArea();
            if (user is not null && !string.IsNullOrWhiteSpace(_script.ConfigPath))
            {
                _statusChanged?.Invoke("正在加载用户配置...");
                if (!_configRun.Prepare(out string? prepError))
                {
                    record.Status = "failed";
                    record.FinalStatus = "failed";
                    record.EndTime = DateTime.Now;
                    record.ResultDetail = $"用户配置加载失败：{prepError}";
                    Logger.Error($"[错误] 脚本「{_script.Name}」用户「{user.Name}」配置加载失败：{prepError}");
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
                        };
                        AppendScriptLog($"===== 第 {attemptNo}/{maxAttempts} 次尝试 开始（{retryAttempt.StartTime:HH:mm:ss}） =====");
                        AppendScriptLog($"===== 第 {attemptNo}/{maxAttempts} 次尝试 结束：failed（{retryAttempt.Reason}） =====");
                        record.AttemptDetails.Add(retryAttempt);
                        Results.CompleteAttempt();
                        record.Attempts = attemptNo;
                        record.Status = "failed";
                        record.FinalStatus = "failed";
                        record.EndTime = retryAttempt.EndTime;
                        record.ResultDetail = retryAttempt.Reason;
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

                Logger.Info($"===== 脚本「{_script.Name}」第 {attemptNo}/{maxAttempts} 次尝试 =====");
                RunAttemptResult result;
                bool runPreRun = AttemptLifecycle.ShouldRunPreRun(
                    user is not null && !string.IsNullOrWhiteSpace(user.PreRunScript),
                    user?.PreRunOnceOnly ?? false,
                    _preRunCompletedSuccessfully);
                bool mainExecuted = true;
                if (runPreRun)
                {
                    RunAttemptResult? preResult = await _attemptRunner.RunUserScriptAsync(user!.PreRunScript!, "任务前", attempt, OperationToken).ConfigureAwait(false);
                    if (preResult is not null)
                    {
                        // PreRun 只有成功（返回 null）才允许进入 Main；失败/取消直接结束本次 Attempt。
                        mainExecuted = false;
                        result = preResult;
                    }
                    else
                    {
                        _preRunCompletedSuccessfully = true;
                        result = await _attemptRunner.RunAsync(attempt).ConfigureAwait(false);
                    }
                }
                else
                {
                    result = await _attemptRunner.RunAsync(attempt).ConfigureAwait(false);
                }

                if (mainExecuted && result.Status != "cancelled"
                    && user is not null && !string.IsNullOrWhiteSpace(user.PostRunScript)
                    && AttemptLifecycle.ShouldRunPostRun(
                        user.PostRunOnFinalOnly,
                        attemptNo,
                        retryPolicy,
                        result))
                {
                    RunAttemptResult? postResult = await _attemptRunner.RunUserScriptAsync(user!.PostRunScript!, "任务后", attempt, OperationToken).ConfigureAwait(false);
                    if (postResult is not null)
                    {
                        result = RunAttemptResult.MergePostRun(result, postResult);
                    }
                }

                attempt.EndTime = DateTime.Now;
                attempt.Status = result.Status;
                attempt.Reason = result.Reason;
                record.Attempts = attemptNo;
                if (!string.IsNullOrWhiteSpace(result.NotifyText))
                {
                    record.CustomNotifyText = result.NotifyText;
                }
                AppendScriptLog($"===== 第 {attemptNo}/{maxAttempts} 次尝试 结束：{result.Status}（{result.Reason}） =====");
                Logger.Info($"第 {attemptNo} 次尝试结束：{result.Status}（{result.Reason}）");
                Results.CompleteAttempt();

                if (result.Status == "success")
                {
                    record.Status = "success";
                    record.EndTime = DateTime.Now;
                    record.ResultDetail = attemptNo == 1 ? "一次成功" : $"第 {attemptNo} 次尝试成功";
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
                    break;
                }
            }

            if (record.Status == "success")
            {
                string full = _scriptFullLog.ToString();
                bool hasErrorKeyword = full.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0
                    || full.IndexOf("错误", StringComparison.Ordinal) >= 0
                    || full.IndexOf("异常", StringComparison.Ordinal) >= 0
                    || full.IndexOf("失败", StringComparison.Ordinal) >= 0;
                record.FinalStatus = record.Attempts > 1 || hasErrorKeyword ? "partial" : "success";
            }
            else
            {
                record.FinalStatus = record.Status;
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
                    Logger.Error($"[错误] 脚本「{_script.Name}」用户「{user?.Name ?? _userName ?? ""}」配置还原失败：{restoreError}");
                }
            }
        }
    }

    /// <summary>运行用户自写的前置/后置脚本：启动并等待退出，退出码非 0 视为失败；支持超时与取消。</summary>
    internal async Task<RunAttemptResult?> RunUserScriptCoreAsync(string scriptPath, string role, RunAttempt attempt, CancellationToken token)
    {
        if (!TextRules.IsExecutable(scriptPath))
        {
            return RunAttemptResult.Failed($"{role}脚本路径错误或不是可执行文件：{scriptPath}");
        }
        string workingDir = string.IsNullOrWhiteSpace(_script.RootPath)
            ? Path.GetDirectoryName(scriptPath) ?? ""
            : _script.RootPath;
        var psi = SystemActions.BuildScriptStartInfo(scriptPath, workingDir, Array.Empty<string>(), noWindow: true, redirect: true);
        ProcessOwnership? userOwnership = ProcessOwnership.TryCreate(role);
        Process? process;
        try
        {
            process = SystemActions.StartOwnedProcess(psi, userOwnership);
        }
        catch (Exception ex)
        {
            userOwnership?.Dispose();
            return RunAttemptResult.Failed($"{role}脚本启动失败：{ex.Message}");
        }
        if (process is null)
        {
            userOwnership?.Dispose();
            return RunAttemptResult.Failed($"{role}脚本启动失败：未能创建进程");
        }
        _statusChanged?.Invoke($"{role}脚本已启动（PID {process.Id}）");
        Logger.Info($"[{(_mode == "auto" ? "自动" : "手动")}运行] 脚本「{_script.Name}」{role}脚本已启动：{scriptPath}（PID {process.Id}）");

        void OnConsoleData(string? data)
        {
            if (string.IsNullOrWhiteSpace(data))
            {
                return;
            }
            _logLine?.Invoke(data);
        }

        process.OutputDataReceived += (_, e) => OnConsoleData(e.Data);
        process.ErrorDataReceived += (_, e) => OnConsoleData(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource();
        if (_script.TotalTimeoutMinutes > 0)
        {
            double remainingSeconds = RemainingRunSeconds();
            if (remainingSeconds <= 0)
            {
                SystemActions.KillOwnedProcessTree(userOwnership, process.Id, scriptPath, role);
                process.Dispose();
                userOwnership?.Dispose();
                return RunAttemptResult.Fatal($"运行总时间超过限制（{_script.TotalTimeoutMinutes} 分钟）");
            }
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(remainingSeconds));
        }
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(combined.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !token.IsCancellationRequested)
        {
            bool cleaned = SystemActions.KillOwnedProcessTree(userOwnership, process.Id, scriptPath, role);
            if (!cleaned)
            {
                _configRun?.MarkProcessCleanupUnconfirmed($"{role}脚本超时后仍未确认退出");
            }
            process.Dispose();
            userOwnership?.Dispose();
            return RunAttemptResult.Fatal($"{role}脚本运行超时（{_script.TotalTimeoutMinutes} 分钟）");
        }
        catch (OperationCanceledException) when (_budgetExpired || _budget?.IsExpired == true)
        {
            bool cleaned = SystemActions.KillOwnedProcessTree(userOwnership, process.Id, scriptPath, role);
            if (!cleaned)
            {
                _configRun?.MarkProcessCleanupUnconfirmed($"{role}脚本总预算耗尽后仍未确认退出");
            }
            process.Dispose();
            userOwnership?.Dispose();
            return RunAttemptResult.Fatal($"运行总时间超过限制（{_script.TotalTimeoutMinutes} 分钟）");
        }
        catch (OperationCanceledException)
        {
            bool cleaned = SystemActions.KillOwnedProcessTree(userOwnership, process.Id, scriptPath, role);
            if (!cleaned)
            {
                _configRun?.MarkProcessCleanupUnconfirmed($"{role}脚本取消后仍未确认退出");
                process.Dispose();
                userOwnership?.Dispose();
                return RunAttemptResult.Fatal($"{role}脚本取消后进程清理未确认，已保留配置现场");
            }
            process.Dispose();
            userOwnership?.Dispose();
            return RunAttemptResult.Cancelled($"已取消（{role}脚本执行期间）");
        }
        bool hasExited = process.HasExited;
        int exitCode = process.ExitCode;
        bool cleanedAfterExit = SystemActions.KillOwnedProcessTree(userOwnership, process.Id, scriptPath, role);
        process.Dispose();
        userOwnership?.Dispose();
        if (!cleanedAfterExit)
        {
            _configRun?.MarkProcessCleanupUnconfirmed($"{role}脚本退出后仍有未确认的 owned 进程");
            return RunAttemptResult.Fatal($"{role}脚本进程清理未确认，已保留配置现场");
        }
        return hasExited && exitCode == 0 ? null : RunAttemptResult.Failed($"{role}脚本执行失败（退出码 {exitCode}）");
    }

    internal async Task<RunAttemptResult> RunAttemptCoreAsync(RunAttempt attempt)
    {
        string modeText = _mode == "auto" ? "自动" : "手动";
        var cleanup = new CleanupManager(_script, modeText, () => _emulatorDriver);
        RunAttemptResult? budgetError = CheckTotalTimeout();
        if (budgetError is not null)
        {
            return budgetError;
        }
        // Attempt 起点日志环境：一次性记录日志格式下所有候选的 path/FileId/length；后续通配符轮换按这张快照决定读取起点。
        var logEnv = new AttemptLogEnvironment(_script, modeText);

        async Task<RunAttemptResult> FinishEarlyAsync(RunAttemptResult early)
        {
            await cleanup.CleanupGameOnEarlyExitAsync(early).ConfigureAwait(false);
            return early;
        }

        if (_script.LaunchGame)
        {
            if (string.IsNullOrWhiteSpace(_script.GameExe))
            {
                Logger.Info($"[{modeText}运行] 脚本「{_script.Name}」未填写游戏路径，跳过游戏启动。");
            }
            else if (EmulatorSupport.IsEmulator(_script))
            {
                RunAttemptResult? emuError = await LaunchEmulatorGameAsync(modeText).ConfigureAwait(false);
                if (emuError is not null)
                {
                    return await FinishEarlyAsync(emuError).ConfigureAwait(false);
                }
            }
            else
            {
                if (!TextRules.IsExecutable(_script.GameExe))
                {
                    return await FinishEarlyAsync(RunAttemptResult.Failed("游戏路径错误或不是可执行文件")).ConfigureAwait(false);
                }
                _statusChanged?.Invoke("正在启动游戏...");
                try
                {
                    string gameWork = Path.GetDirectoryName(_script.GameExe) ?? "";
                    bool commandFile = SystemActions.IsCommandFile(_script.GameExe);
                    ProcessStartInfo gamePsi = SystemActions.BuildScriptStartInfo(_script.GameExe, gameWork, TextRules.SplitArgs(_script.GameArgs), noWindow: false, redirect: commandFile);
                    if (!commandFile)
                    {
                        gamePsi.UseShellExecute = true;
                    }
                    Process? gameProcess = SystemActions.StartWithOutputDrain(gamePsi, disposeWhenExited: true);
                    int gamePid = gameProcess?.Id ?? 0;
                    if (gamePid > 0)
                    {
                        SystemActions.BringToFrontFireAndForget(gamePid, "游戏");
                    }
                    Logger.Info($"游戏已启动：{_script.GameExe}（等待 {_script.GameWaitSeconds} 秒确认）。");
                }
                catch (Exception ex)
                {
                    return await FinishEarlyAsync(RunAttemptResult.Failed($"游戏启动失败：{ex.Message}")).ConfigureAwait(false);
                }
                double remainingSeconds = RemainingRunSeconds();
                if (remainingSeconds <= 0)
                {
                    return await FinishEarlyAsync(RunAttemptResult.Fatal($"运行总时间超过限制（{_script.TotalTimeoutMinutes} 分钟）")).ConfigureAwait(false);
                }
                double requestedGameWait = TestHooks.ScaledSeconds(Math.Max(0, _script.GameWaitSeconds));
                bool gameConfirmed;
                try
                {
                    gameConfirmed = await WaitForGameProcessAsync(TimeSpan.FromSeconds(Math.Min(requestedGameWait, remainingSeconds))).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return await FinishEarlyAsync(RunAttemptResult.Cancelled("已取消（等待游戏启动期间）")).ConfigureAwait(false);
                }
                if (RemainingRunSeconds() <= 0)
                {
                    return await FinishEarlyAsync(RunAttemptResult.Fatal($"运行总时间超过限制（{_script.TotalTimeoutMinutes} 分钟）")).ConfigureAwait(false);
                }
                if (!gameConfirmed)
                {
                    return await FinishEarlyAsync(RunAttemptResult.Failed($"等待 {_script.GameWaitSeconds} 秒后仍未检测到游戏进程，游戏可能启动失败")).ConfigureAwait(false);
                }
                _statusChanged?.Invoke("已确认游戏进程启动");
                Logger.Info($"[{modeText}运行] 脚本「{_script.Name}」已确认游戏进程启动，继续运行脚本。");
            }
        }

        if (!TextRules.IsExecutable(_script.MainExe))
        {
            return await FinishEarlyAsync(RunAttemptResult.Failed("脚本主程序路径错误或不是可执行文件")).ConfigureAwait(false);
        }

        string workingDir = string.IsNullOrWhiteSpace(_script.RootPath)
            ? Path.GetDirectoryName(_script.MainExe) ?? ""
            : _script.RootPath;

        (string launchExe, List<string> launchArgs) = SystemActions.ResolveLaunchTarget(_script.MainExe, workingDir, _script.Args);

        Process? process = null;
        bool stdoutAttached = false;
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
        ProcessOwnership? ownership = ProcessOwnership.TryCreate("脚本");
        _processOwnership = ownership;
        var psi = SystemActions.BuildScriptStartInfo(launchExe, workingDir, launchArgs, noWindow: true, redirect: true);
        try
        {
            process = SystemActions.StartOwnedProcess(psi, ownership);
            stdoutAttached = true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            _processOwnership = null;
            ownership?.Dispose();
            return await FinishEarlyAsync(RunAttemptResult.Fatal($"脚本启动失败：目标程序要求管理员权限（{launchExe}）。NexusPipeline 已以管理员身份运行仍被拒绝时，请检查目标程序的权限配置")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _processOwnership = null;
            ownership?.Dispose();
            return await FinishEarlyAsync(RunAttemptResult.Failed($"脚本启动失败：{ex.Message}")).ConfigureAwait(false);
        }
        if (process is null)
        {
            _processOwnership = null;
            ownership?.Dispose();
            return await FinishEarlyAsync(RunAttemptResult.Failed("脚本启动失败：未能创建进程")).ConfigureAwait(false);
        }
        // 运行脚本实例/调度队列时脚本主窗口最小化让位（命令行/日志已接管输出；控制台脚本无窗口自动跳过），
        // 游戏窗口另由 BringToFrontFireAndForget 前置以利截图识别。
        SystemActions.MinimizeWindowFireAndForget(process.Id, "脚本");
        _statusChanged?.Invoke($"脚本已启动（PID {process.Id}）");
        Logger.Info($"[{modeText}运行] 脚本「{_script.Name}」已启动：{launchExe}（PID {process.Id}）");

        // 统一游戏窗口前置——无论 LaunchGame 配置（true 由宿主启动、false 由启动器/用户拉起），
        // 只要检测到游戏进程存在即前置其窗口（截图识别需要游戏画面在最前；游戏启动方式复杂由脚本适配，宿主不重复启动）。
        // 模拟器模式跳过（adb 命令行工具，无窗口前置需求）。
        if (!EmulatorSupport.IsEmulator(_script))
        {
            BringGameToFrontIfRunning();
        }

        void OnConsoleData(string? data)
        {
            if (string.IsNullOrWhiteSpace(data))
            {
                return;
            }
            _logLine?.Invoke(data);
        }

        if (process is not null && stdoutAttached)
        {
            process.OutputDataReceived += (_, e) => OnConsoleData(e.Data);
            process.ErrorDataReceived += (_, e) => OnConsoleData(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        bool KillScriptAndConfirm()
        {
            bool confirmed = cleanup.KillScript(process, launchExe, excludeGame, _processOwnership);
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
        DateTime? firstEntryAt = null;
        RunAttemptResult? result = null;

        string attemptId = $"{_script.Id}:{attempt.Number}:{attempt.StartTime.Ticks}";

        JudgeSnapshot CaptureJudgeSnapshot(int generation)
        {
            string scriptDir = _configRun?.ScriptDir
                ?? UserConfigManager.ScriptDir(_script.Id, _resolvedUser?.UserKey ?? _activeUser?.Name);
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
            ScriptUser? userSnapshot = _activeUser?.Clone();
            string inputJson = JudgeScriptRunner.BuildInput(scriptSnapshot, userSnapshot, files, scriptDir, logText, logTruncated);
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
            });
        var terminator = new AttemptTerminator(workers, judge, status => _statusChanged?.Invoke(status));
        await using var workersScope = workers;

        try
        {
            while (result is null)
            {
                OperationToken.ThrowIfCancellationRequested();
                workers.ConsumeConfigSyncResult();
                workers.ConsumeJudgeResult();
                workers.TryQueuePendingFinalJudge();
                result = terminator.TryApplyFinalDecision();
                if (result is not null)
                {
                    break;
                }

                // 自动更新配置首次检测——仅第 1 次尝试、运行开始 15 秒（缩放）后同步一次
                // config → store（捕获脚本启动后自行更新的任务配置；重试轮不检测）。
                // 并入主循环避免后台任务与收尾还原的竞态；关/开模式共有。
                if (!_firstSyncDone && attempt.Number == 1 && _configRun is not null && _configRun.IsPrepared
                    && ShouldRunFirstSync(_budget!.ElapsedSeconds, TestHooks.ScaledSeconds(15)))
                {
                    _firstSyncDone = true;
                    Logger.Info($"[{modeText}运行] 脚本「{_script.Name}」自动更新配置首次检测（运行开始 15 秒后）。");
                    if (!workers.TryStartConfigSync(new ConfigSyncRequest(attemptId, attempt.Number, true, DateTime.Now)))
                    {
                        Logger.Info($"[{modeText}运行] 脚本「{_script.Name}」配置同步 worker 当前繁忙，本次首次检测已并入已有同步。");
                    }
                }

                // 游戏由启动器延迟拉起（启动瞬间检测不到），运行期间每轮检测，出现即前置一次。
                // 模拟器模式跳过窗口前置。
                if (!EmulatorSupport.IsEmulator(_script))
                {
                    BringGameToFrontIfRunning();
                }

                if (judge.IsFailure)
                {
                    if (KillScriptAndConfirm())
                    {
                        result = RunAttemptResult.Failed(judge.Reason ?? "日志出现失败关键字，任务判定失败");
                        result.NotifyText = judge.NotifyText;
                    }
                    else
                    {
                        result = RunAttemptResult.Fatal("脚本进程清理未确认，已阻断配置替换与重试");
                    }
                    break;
                }

                if (_script.TotalTimeoutMinutes > 0
                    && _budget!.IsExpired)
                {
                    result = RunAttemptResult.Fatal($"运行总时间超过限制（{_script.TotalTimeoutMinutes} 分钟）");
                    break;
                }

                monitor = logEnv.RefreshMonitor(monitor);

                string newContent = attemptMonitor.ReadLog(monitor);
                if (newContent.Length > 0)
                {
                    firstEntryAt ??= DateTime.Now;
                    foreach (string line in newContent.Split('\n').Select(l => l.TrimEnd('\r')))
                    {
                        if (line.Trim().Length == 0)
                        {
                            continue;
                        }
                        _logLine?.Invoke(line);
                        AppendScriptLog(line);
                        switch (judge.HandleLine(line))
                        {
                            case SessionJudge.LineHit.SuccessKeyword:
                                _statusChanged?.Invoke("已检测到成功关键字，等待脚本退出...");
                                Logger.Info($"[{modeText}运行] 脚本「{_script.Name}」日志出现成功关键字。");
                                break;
                            case SessionJudge.LineHit.FailureKeyword:
                                _statusChanged?.Invoke("已检测到失败关键字，任务判定失败");
                                Logger.Info($"[{modeText}运行] 脚本「{_script.Name}」日志出现失败关键字，任务判定失败。");
                                break;
                        }
                    }
                }

                // （台账外，修正）：周期触发与退出/stall 最终触发同轮先后命中时跳过最终触发——
                // 周期触发输入为「无新内容」状态，同轮内日志段不变，最终触发属完全重复执行；
                // 批次触发（有新内容）后的同轮最终触发**必须保留**（进程退出是新事实，判断脚本可能
                // 基于自身状态文件在第二次执行给出最终判定，如计数器——06 spec「进程退出时最终触发」用例）。
                bool skipFinalJudge = false;
                if (scriptMode && newContent.Length > 0 && result is null && !judge.IsMarker)
                {
                    workers.QueueJudge(final: false);
                }
                else if (scriptMode && newContent.Length == 0 && result is null
                    && firstEntryAt is not null && !judge.IsMarker && (DateTime.Now - judge.LastJudgeAt).TotalSeconds >= TestHooks.ScaledSeconds(30))
                {
                    _statusChanged?.Invoke("日志无新内容，周期触发判断脚本...");
                    skipFinalJudge = true;
                    workers.QueueJudge(final: false);
                }

                workers.ConsumeJudgeResult();
                result = terminator.TryApplyFinalDecision();
                if (result is not null)
                {
                    break;
                }

                bool scriptExited = attemptMonitor.IsScriptExited(process, launchExe, _processOwnership, excludeGame);
                if (scriptExited)
                {
                    result = terminator.OnScriptExited(monitor is null, !string.IsNullOrWhiteSpace(_script.LogPath), skipFinalJudge);
                    if (result is not null)
                    {
                        break;
                    }
                }

                if (terminator.TerminalObservation)
                {
                    await Task.Delay(TestHooks.ScaledMs(50), OperationToken).ConfigureAwait(false);
                    continue;
                }

                if (!judge.IsMarker)
                {
                    StallObservation stall = attemptMonitor.CheckStall(
                        monitor,
                        !string.IsNullOrWhiteSpace(_script.LogPath),
                        attemptStart,
                        firstEntryAt,
                        _script.LogStallTimeoutMinutes);
                    if (stall.Hit)
                    {
                        result = terminator.OnStall(stall, skipFinalJudge);
                        if (result is not null)
                        {
                            break;
                        }
                    }
                }

                if (judge.IsMarker
                    && (DateTime.Now - judge.MarkerSeenAt!.Value).TotalSeconds >= TestHooks.ScaledSeconds(ExitGraceSecondsAfterMarker))
                {
                    result = KillScriptAndConfirm()
                        ? RunAttemptResult.Success(judge.Reason ?? "完成标志已出现，等待退出超时后已终止脚本，判定成功")
                        : RunAttemptResult.Fatal("脚本进程清理未确认，已阻断配置替换与重试");
                    result.NotifyText = judge.NotifyText;
                    break;
                }

                await Task.Delay(TestHooks.ScaledMs(1000), OperationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_budgetExpired || _budget?.IsExpired == true)
        {
            result = RunAttemptResult.Fatal($"运行总时间超过限制（{_script.TotalTimeoutMinutes} 分钟）");
        }
        catch (OperationCanceledException)
        {
            result = RunAttemptResult.Cancelled("运行已取消");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 脚本「{_script.Name}」监控异常：{ex.Message}");
            result = RunAttemptResult.Failed($"监控异常：{ex.Message}");
        }

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
        // 运行收尾后释放进程句柄（此前未 Dispose，句柄延迟到 GC）。
        process?.Dispose();
        process = null;
        try
        {
            await cleanup.CleanupGameAsync(finalResult, attempt.Number, Math.Max(1, _script.MaxAttempts)).ConfigureAwait(false);
        }
        finally
        {
            _processOwnership = null;
            ownership?.Dispose();
        }
        return finalResult;
    }

    /// <summary>
    /// 等待并确认游戏进程启动：每 1 秒轮询进程是否出现，上限为超时时间。
    /// bat/cmd 启动器经 cmd.exe 包装无法按名检测（IsExeRunning 返回 false），直接按已启动放行并等待到超时结束（保持原有等待语义）。
    /// </summary>
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

    /// <summary>
    /// 模拟器模式游戏启动：连接模拟器 → am start 启动应用 → 前台确认。
    /// 目标包名从启动参数 -n 解析；解析不到时仅确认 adb connect 与 am start 命令成功（宽松兜底）。
    /// 返回 null = 成功，否则为失败结果。
    /// </summary>
    private async Task<RunAttemptResult?> LaunchEmulatorGameAsync(string modeText)
    {
        try
        {
            return await LaunchEmulatorGameCoreAsync(modeText).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return RunAttemptResult.Cancelled("已取消（启动模拟器应用期间）");
        }
    }

    private async Task<RunAttemptResult?> LaunchEmulatorGameCoreAsync(string modeText)
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
        if (_emulatorDriver is null)
        {
            _emulatorTarget = await EmulatorDetector.DetectAsync(
                _script.GameExe,
                OperationToken,
                RemainingCommandSeconds(30)).ConfigureAwait(false);
            if (_emulatorTarget.Kind == EmulatorKind.DetectionError)
            {
                return RunAttemptResult.Failed(_emulatorTarget.DetectionError ?? "模拟器目标识别失败");
            }
            _emulatorDriver = EmulatorDriverFactory.Create(_emulatorTarget);
            Logger.Info($"[{modeText}运行] 脚本「{_script.Name}」已冻结模拟器驱动：{_emulatorDriver.Kind}（目标 {_script.GameExe}）。");
        }
        _statusChanged?.Invoke("正在连接模拟器...");
        if (RemainingRunSeconds() <= 0)
        {
            return RunAttemptResult.Fatal($"运行总时间超过限制（{_script.TotalTimeoutMinutes} 分钟）");
        }
        EmulatorCommandResult ready = await _emulatorDriver.EnsureReadyAsync(OperationToken, RemainingCommandSeconds(30)).ConfigureAwait(false);
        if (RemainingRunSeconds() <= 0)
        {
            return RunAttemptResult.Fatal($"运行总时间超过限制（{_script.TotalTimeoutMinutes} 分钟）");
        }
        if (!ready.Ok)
        {
            return RunAttemptResult.Failed($"模拟器连接/准备失败（{_script.GameExe}）：{ready.Output.Trim()}");
        }
        _statusChanged?.Invoke("正在启动模拟器应用...");
        if (RemainingRunSeconds() <= 0)
        {
            return RunAttemptResult.Fatal($"运行总时间超过限制（{_script.TotalTimeoutMinutes} 分钟）");
        }
        EmulatorCommandResult start = await _emulatorDriver.StartAppAsync(
            startArgs,
            OperationToken,
            RemainingCommandSeconds(30)).ConfigureAwait(false);
        if (RemainingRunSeconds() <= 0)
        {
            return RunAttemptResult.Fatal($"运行总时间超过限制（{_script.TotalTimeoutMinutes} 分钟）");
        }
        if (!start.Ok)
        {
            return RunAttemptResult.Failed($"模拟器应用启动失败：{start.Output.Trim()}");
        }
        Logger.Info($"[{modeText}运行] 脚本「{_script.Name}」模拟器应用启动命令已执行（{_script.GameExe}，等待 {_script.GameWaitSeconds} 秒确认前台）。");
        string? targetPkg = EmulatorSupport.ParseAmStartPackage(_script.GameArgs);
        bool confirmed = targetPkg is null
            ? true
            : await WaitForEmulatorAppAsync(TimeSpan.FromSeconds(Math.Min(TestHooks.ScaledSeconds(Math.Max(0, _script.GameWaitSeconds)), RemainingRunSeconds())), targetPkg).ConfigureAwait(false);
        if (RemainingRunSeconds() <= 0)
        {
            return RunAttemptResult.Fatal($"运行总时间超过限制（{_script.TotalTimeoutMinutes} 分钟）");
        }
        if (!confirmed)
        {
            return RunAttemptResult.Failed($"等待 {_script.GameWaitSeconds} 秒后模拟器前台未出现应用（{targetPkg}），应用可能启动失败");
        }
        _statusChanged?.Invoke("已确认模拟器应用启动");
        Logger.Info($"[{modeText}运行] 脚本「{_script.Name}」已确认模拟器应用启动，继续运行脚本。");
        return null;
    }

    /// <summary>等待并确认模拟器前台应用为目标包名：每 1 秒轮询 dumpsys window 前台，上限为超时时间。</summary>
    private async Task<bool> WaitForEmulatorAppAsync(TimeSpan timeout, string targetPackage)
    {
        if (_emulatorDriver is null)
        {
            return false;
        }
        DateTime deadline = DateTime.Now + timeout;
        while (true)
        {
            if (RemainingRunSeconds() <= 0)
            {
                return false;
            }
            string? foreground = await _emulatorDriver.GetForegroundPackageAsync(
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

    private int RemainingCommandSeconds(int cap)
    {
        return _budget?.RemainingCommandSeconds(cap) ?? cap;
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
            Process[] procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(_script.GameExe));
            try
            {
                if (procs.Length > 0)
                {
                    SystemActions.BringToFrontFireAndForget(procs[0].Id, "游戏");
                    _gameFronted = true;
                }
            }
            finally
            {
                foreach (Process proc in procs)
                {
                    proc.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 检测游戏进程失败：{ex.Message}");
        }
    }
}
