using System.Diagnostics;
using System.Text;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

internal class RunAttemptResult
{
    public string Status { get; set; } = "";

    public string Reason { get; set; } = "";

    public bool IsFatal { get; set; }

    /// <summary>判断脚本返回的自定义通知文本（可选）。</summary>
    public string NotifyText { get; set; } = "";

    public static RunAttemptResult Success(string reason)
    {
        return new RunAttemptResult { Status = "success", Reason = reason };
    }

    public static RunAttemptResult Failed(string reason)
    {
        return new RunAttemptResult { Status = "failed", Reason = reason };
    }

    public static RunAttemptResult Fatal(string reason)
    {
        return new RunAttemptResult { Status = "failed", Reason = reason, IsFatal = true };
    }

    public static RunAttemptResult Cancelled(string reason)
    {
        return new RunAttemptResult { Status = "cancelled", Reason = reason, IsFatal = true };
    }
}

internal class RunSession
{
    /// <summary>成功判定后等待脚本自行退出的宽限秒数（NEXUS_TIME_SCALE 加速时按比例缩放）。</summary>
    private const int ExitGraceSecondsAfterMarker = 60;

    private const int MaxScriptLogBytes = 20 * 1024 * 1024;

    private readonly ScriptInstance _script;

    private readonly string _mode;

    private readonly string _queueId;

    private readonly string _queueName;

    private readonly string? _userName;

    private readonly CancellationToken _token;

    private readonly Action<int, int>? _attemptChanged;

    private readonly Action<string>? _statusChanged;

    private readonly Action<string>? _logLine;

    private readonly StringBuilder _scriptFullLog = new();

    private bool _scriptLogTruncated;

    /// <summary>整个运行（含全部重试与前置/后置脚本）的开始时间：TotalTimeoutMinutes 以它为准。</summary>
    private DateTime _runStartedAt;

    private ScriptUser? _activeUser;

    /// <summary>当前尝试在运行日志中的起点：判断脚本输入只取本次尝试的日志段（上次尝试的判定/残留行不跨尝试污染）。</summary>
    private int _attemptLogStart;

    /// <summary>每尝试脚本日志段（v0.5.3+ 按尝试分批落盘，运行结束随历史保存）。</summary>
    private readonly List<string> _attemptLogSegments = new();

    public RunSession(ScriptInstance script, string mode, string queueId, string queueName, string? userName, CancellationToken token,
        Action<int, int>? attemptChanged = null, Action<string>? statusChanged = null, Action<string>? logLine = null)
    {
        _script = script;
        _mode = mode;
        _queueId = queueId;
        _queueName = queueName;
        _userName = userName;
        _token = token;
        _attemptChanged = attemptChanged;
        _statusChanged = statusChanged;
        _logLine = logLine;
    }

    /// <summary>每尝试脚本日志段（按尝试分批落盘）。</summary>
    public List<string> AttemptLogs
    {
        get
        {
            return _attemptLogSegments;
        }
    }

    private void AppendScriptLog(string line)
    {
        if (_scriptLogTruncated)
        {
            return;
        }
        if (_scriptFullLog.Length > MaxScriptLogBytes)
        {
            _scriptLogTruncated = true;
            _scriptFullLog.AppendLine("（脚本日志超过 20MB，已截断尾部）");
            return;
        }
        _scriptFullLog.AppendLine(line);
    }

    public async Task<RunRecord> RunAsync()
    {
        _runStartedAt = DateTime.Now;
        var record = new RunRecord
        {
            ScriptInstanceId = _script.Id,
            ScriptName = _script.Name,
            QueueId = _queueId,
            QueueName = _queueName,
            Mode = _mode,
            UserName = _userName ?? "",
            StartTime = DateTime.Now,
        };

        ScriptUser? user = string.IsNullOrWhiteSpace(_userName)
            ? null
            : UserConfigManager.FindEnabledUser(_script, _userName);
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

        if (_script.HasJudgeScript())
        {
            UserConfigManager.PrepareScriptDir(_script.Id, user?.Name);
        }

        bool configPrepared = false;
        if (user is not null && !string.IsNullOrWhiteSpace(_script.ConfigPath))
        {
            _statusChanged?.Invoke("正在加载用户配置...");
            if (!UserConfigManager.PrepareForRun(_script.Id, user.Name, _script.ConfigPath, out string? prepError))
            {
                record.Status = "failed";
                record.FinalStatus = "failed";
                record.EndTime = DateTime.Now;
                record.ResultDetail = $"用户配置加载失败：{prepError}";
                Logger.Error($"[错误] 脚本「{_script.Name}」用户「{user.Name}」配置加载失败：{prepError}");
                return record;
            }
            configPrepared = true;
        }

        int maxAttempts = Math.Max(1, _script.MaxAttempts);
        record.MaxAttempts = maxAttempts;
        _attemptChanged?.Invoke(1, maxAttempts);

        try
        {
            for (int attemptNo = 1; attemptNo <= maxAttempts; attemptNo++)
            {
                _attemptChanged?.Invoke(attemptNo, maxAttempts);
                var attempt = new RunAttempt
                {
                    Number = attemptNo,
                    StartTime = DateTime.Now,
                };
                record.AttemptDetails.Add(attempt);
                AppendScriptLog($"===== 第 {attemptNo}/{maxAttempts} 次尝试 开始（{attempt.StartTime:HH:mm:ss}） =====");

                Logger.Info($"===== 脚本「{_script.Name}」第 {attemptNo}/{maxAttempts} 次尝试 =====");
                RunAttemptResult result;
                if (user is not null && !string.IsNullOrWhiteSpace(user.PreRunScript)
                    && (attemptNo == 1 || !user.PreRunOnceOnly))
                {
                    RunAttemptResult? preResult = await RunUserScriptAsync(user.PreRunScript, "任务前", attempt, _token).ConfigureAwait(false);
                    result = preResult ?? await RunAttemptAsync(attempt).ConfigureAwait(false);
                }
                else
                {
                    result = await RunAttemptAsync(attempt).ConfigureAwait(false);
                }

                if (result.Status != "cancelled"
                    && user is not null && !string.IsNullOrWhiteSpace(user.PostRunScript)
                    && (attemptNo >= maxAttempts || !user.PostRunOnFinalOnly))
                {
                    RunAttemptResult? postResult = await RunUserScriptAsync(user.PostRunScript, "任务后", attempt, _token).ConfigureAwait(false);
                    if (postResult is not null)
                    {
                        postResult.NotifyText = result.NotifyText;
                        result = postResult;
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
                _attemptLogSegments.Add(_scriptFullLog.ToString(_attemptLogStart, _scriptFullLog.Length - _attemptLogStart));

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
                if (attemptNo >= maxAttempts)
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
            if (_script.HasJudgeScript())
            {
                // 先还原配置替换（swap-backup → config 恢复为替换前快照内容），
                // 再执行配置交换还原（original → config 恢复运行前现场），避免替换还原覆盖交换还原的现场。
                UserConfigManager.RestoreConfigReplacements(_script.Id, user?.Name);
                UserConfigManager.CleanupScriptArea(_script.Id, user?.Name);
            }
            if (configPrepared)
            {
                string? restoreError = UserConfigManager.RestoreAfterRun(_script.Id, user!.Name, _script.ConfigPath);
                if (restoreError is not null)
                {
                    string msg = $"（警告：配置还原失败，现场已保留，详见日志）";
                    record.ResultDetail += msg;
                    Logger.Error($"[错误] 脚本「{_script.Name}」用户「{user.Name}」配置还原失败：{restoreError}");
                }
            }
        }
    }

    /// <summary>运行用户自写的前置/后置脚本：启动并等待退出，退出码非 0 视为失败；支持超时与取消。</summary>
    private async Task<RunAttemptResult?> RunUserScriptAsync(string scriptPath, string role, RunAttempt attempt, CancellationToken token)
    {
        if (!TextRules.IsExecutable(scriptPath))
        {
            return RunAttemptResult.Failed($"{role}脚本路径错误或不是可执行文件：{scriptPath}");
        }
        string workingDir = string.IsNullOrWhiteSpace(_script.RootPath)
            ? Path.GetDirectoryName(scriptPath) ?? ""
            : _script.RootPath;
        var psi = SystemActions.BuildScriptStartInfo(scriptPath, workingDir, Array.Empty<string>(), noWindow: true, redirect: true);
        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return RunAttemptResult.Failed($"{role}脚本启动失败：{ex.Message}");
        }
        if (process is null)
        {
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
            double remainingSeconds = TestHooks.ScaledSeconds(_script.TotalTimeoutMinutes * 60) - (DateTime.Now - _runStartedAt).TotalSeconds;
            if (remainingSeconds <= 0)
            {
                SystemActions.KillTree(process.Id);
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
            SystemActions.KillTree(process.Id);
            return RunAttemptResult.Failed($"{role}脚本运行超时（{_script.TotalTimeoutMinutes} 分钟）");
        }
        catch (OperationCanceledException)
        {
            SystemActions.KillTree(process.Id);
            return RunAttemptResult.Cancelled($"已取消（{role}脚本执行期间）");
        }
        bool ok = process.HasExited && process.ExitCode == 0;
        return ok ? null : RunAttemptResult.Failed($"{role}脚本执行失败（退出码 {process.ExitCode}）");
    }

    private async Task<RunAttemptResult> RunAttemptAsync(RunAttempt attempt)
    {
        _attemptLogStart = _scriptFullLog.Length;
        string modeText = _mode == "auto" ? "自动" : "手动";

        if (_script.LaunchGame)
        {
            if (string.IsNullOrWhiteSpace(_script.GameExe))
            {
                Logger.Info($"[{modeText}运行] 脚本「{_script.Name}」未填写游戏路径，跳过游戏启动。");
            }
            else
            {
                if (!TextRules.IsExecutable(_script.GameExe))
                {
                    return RunAttemptResult.Failed("游戏路径错误或不是可执行文件");
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
                        _ = Task.Run(() => SystemActions.BringToFront(gamePid));
                    }
                    Logger.Info($"游戏已启动：{_script.GameExe}（等待 {_script.GameWaitSeconds} 秒确认）。");
                }
                catch (Exception ex)
                {
                    return RunAttemptResult.Failed($"游戏启动失败：{ex.Message}");
                }
                bool gameConfirmed;
                try
                {
                    gameConfirmed = await WaitForGameProcessAsync(TimeSpan.FromSeconds(Math.Max(0, _script.GameWaitSeconds))).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return RunAttemptResult.Cancelled("已取消（等待游戏启动期间）");
                }
                if (!gameConfirmed)
                {
                    return RunAttemptResult.Failed($"等待 {_script.GameWaitSeconds} 秒后仍未检测到游戏进程，游戏可能启动失败");
                }
                _statusChanged?.Invoke("已确认游戏进程启动");
                Logger.Info($"[{modeText}运行] 脚本「{_script.Name}」已确认游戏进程启动，继续运行脚本。");
            }
        }

        if (!TextRules.IsExecutable(_script.MainExe))
        {
            return RunAttemptResult.Failed("脚本主程序路径错误或不是可执行文件");
        }

        string workingDir = string.IsNullOrWhiteSpace(_script.RootPath)
            ? Path.GetDirectoryName(_script.MainExe) ?? ""
            : _script.RootPath;

        (string launchExe, List<string> launchArgs) = SystemActions.ResolveLaunchTarget(_script.MainExe, workingDir, _script.Args);

        Process? process = null;
        bool stdoutAttached = false;
        void KillStartedScript()
        {
            if (process is null)
            {
                return;
            }
            // 进程树清理 + 轮询按名强杀直至确认退出（处理「被杀后自重启」的脚本），确保配置还原前进程已完全退出
            SystemActions.KillAndConfirmExited(process.Id, launchExe, "脚本");
        }

        if (SystemActions.IsExeRunning(launchExe))
        {
            Logger.Info($"[{modeText}运行] 脚本「{_script.Name}」检测到已在运行，直接监控其日志（不重复启动）。");
            _statusChanged?.Invoke("检测到脚本已在运行，直接监控其日志...");
        }
        else
        {
            var psi = SystemActions.BuildScriptStartInfo(launchExe, workingDir, launchArgs, noWindow: true, redirect: true);
            try
            {
                process = Process.Start(psi);
                stdoutAttached = true;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740)
            {
                return RunAttemptResult.Fatal($"脚本启动失败：目标程序要求管理员权限（{launchExe}）。NexusPipeline 已以管理员身份运行仍被拒绝时，请检查目标程序的权限配置");
            }
            catch (Exception ex)
            {
                return RunAttemptResult.Failed($"脚本启动失败：{ex.Message}");
            }
            if (process is null)
            {
                return RunAttemptResult.Failed("脚本启动失败：未能创建进程");
            }
            _ = Task.Run(() => SystemActions.BringToFront(process.Id));
            _statusChanged?.Invoke($"脚本已启动（PID {process.Id}）");
            Logger.Info($"[{modeText}运行] 脚本「{_script.Name}」已启动：{launchExe}（PID {process.Id}）");
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

        string? resolvedBeforeStart = string.IsNullOrWhiteSpace(_script.LogPath) ? null : LogPattern.ResolveFile(_script.LogPath);
        // 脚本启动后解析到的文件可能是上一尝试的残留日志：仅当文件在本次尝试开始后写过（严格时间点，无松弛窗口）
        // 才从头读（快速重建场景），否则一律从末尾读（忽略旧内容）。截断/重建由 ReadNew 长度检查与 FileId 检测兜底。
        DateTime attemptStart = DateTime.Now;
        LogMonitor? monitor = resolvedBeforeStart is null ? null : NewMonitor(resolvedBeforeStart, attemptStart, modeText);
        var judge = new SessionJudge(_script);
        bool judgeConfigured = judge.IsConfigured;
        bool scriptMode = judge.ScriptMode;
        DateTime? firstEntryAt = null;
        RunAttemptResult? result = null;

        // 触发一次判断脚本并按结果设置判定/应用配置替换；返回 true 表示已设置成功或失败判定。
        async Task<bool> TriggerJudgeAsync()
        {
            judge.TouchJudge();
            (string status, string reason, string notifyText, List<string> replaceConfigs, string? error) = await RunJudgeOnceAsync().ConfigureAwait(false);
            if (error is not null)
            {
                Logger.Warn($"[{modeText}运行] 脚本「{_script.Name}」判断脚本执行错误（视为继续运行）：{error}");
                return false;
            }
            SessionJudge.JudgeOutcome outcome = judge.ApplyJudgeResult(status, reason, notifyText, replaceConfigs, replace =>
            {
                Logger.Info($"[{modeText}运行] 脚本「{_script.Name}」判断脚本请求替换配置（{replace.Count} 个文件），应用后重试。");
                UserConfigManager.ApplyConfigReplacements(_script.Id, _activeUser?.Name, _script.ConfigPath, replace);
            });
            if (outcome == SessionJudge.JudgeOutcome.Success)
            {
                _statusChanged?.Invoke("判断脚本判定成功，等待脚本退出...");
                Logger.Info($"[{modeText}运行] 脚本「{_script.Name}」判断脚本判定成功：{reason}");
                return true;
            }
            if (outcome == SessionJudge.JudgeOutcome.Failure)
            {
                _statusChanged?.Invoke("判断脚本判定失败");
                Logger.Info($"[{modeText}运行] 脚本「{_script.Name}」判断脚本判定失败：{reason}");
                return true;
            }
            return false;
        }

        // 最终触发一次判断脚本并按结果设置成功/失败判定（失败时替换配置已在 TriggerJudgeAsync 内应用）；返回 true 表示已设置判定。
        async Task<bool> FinalJudgeOnceAsync()
        {
            await TriggerJudgeAsync().ConfigureAwait(false);
            if (judge.IsFailure)
            {
                result = RunAttemptResult.Failed(judge.Reason ?? "日志出现失败关键字，任务判定失败");
                result.NotifyText = judge.NotifyText;
                return true;
            }
            if (judge.IsMarker)
            {
                result = RunAttemptResult.Success(judge.Reason ?? "判断脚本判定成功");
                result.NotifyText = judge.NotifyText;
                return true;
            }
            return false;
        }

        try
        {
            while (result is null)
            {
                _token.ThrowIfCancellationRequested();

                if (judge.IsFailure)
                {
                    KillStartedScript();
                    result = RunAttemptResult.Failed(judge.Reason ?? "日志出现失败关键字，任务判定失败");
                    break;
                }

                if (_script.TotalTimeoutMinutes > 0
                    && (DateTime.Now - _runStartedAt).TotalSeconds >= TestHooks.ScaledSeconds(_script.TotalTimeoutMinutes * 60))
                {
                    result = RunAttemptResult.Fatal($"运行总时间超过限制（{_script.TotalTimeoutMinutes} 分钟）");
                    break;
                }

                if (!string.IsNullOrWhiteSpace(_script.LogPath))
                {
                    string? resolved = LogPattern.ResolveFile(_script.LogPath);
                    if (resolved is not null)
                    {
                        if (monitor is null)
                        {
                            monitor = NewMonitor(resolved, attemptStart, modeText);
                        }
                        else if (!string.Equals(resolved, monitor.Path, StringComparison.OrdinalIgnoreCase))
                        {
                            monitor.Dispose();
                            monitor = NewMonitor(resolved, attemptStart, modeText, rotated: true);
                        }
                        else
                        {
                            try
                            {
                                if (monitor.FileReplaced(resolved))
                                {
                                    monitor.ReopenFromStart();
                                    Logger.Info($"[{modeText}运行] 脚本「{_script.Name}」日志文件被替换，已重新从头读取：{resolved}");
                                }
                            }
                            catch (Exception)
                            {
                            }
                        }
                    }
                }

                string newContent = monitor?.ReadNew() ?? "";
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

                if (scriptMode && newContent.Length > 0 && result is null && !judge.IsMarker)
                {
                    await TriggerJudgeAsync().ConfigureAwait(false);
                }
                else if (scriptMode && newContent.Length == 0 && result is null
                    && firstEntryAt is not null && !judge.IsMarker && (DateTime.Now - judge.LastJudgeAt).TotalSeconds >= TestHooks.ScaledSeconds(30))
                {
                    _statusChanged?.Invoke("日志无新内容，周期触发判断脚本...");
                    await TriggerJudgeAsync().ConfigureAwait(false);
                }

                bool scriptExited = process is null
                    ? !SystemActions.IsExeRunning(launchExe)
                    : process.HasExited && !SystemActions.IsExeRunning(launchExe);
                if (scriptExited)
                {
                    if (monitor is null && !string.IsNullOrWhiteSpace(_script.LogPath))
                    {
                        if (scriptMode)
                        {
                            _statusChanged?.Invoke("脚本已退出，触发判断脚本最终判定...");
                            await FinalJudgeOnceAsync().ConfigureAwait(false);
                        }
                        result ??= RunAttemptResult.Failed("已配置日志路径但未找到日志文件，进程退出且未检测到完成标志");
                    }
                    else if (monitor is null)
                    {
                        if (scriptMode)
                        {
                            _statusChanged?.Invoke("脚本已退出，触发判断脚本最终判定...");
                            await FinalJudgeOnceAsync().ConfigureAwait(false);
                        }
                        result ??= scriptMode
                            ? RunAttemptResult.Failed("未配置日志路径，判断脚本无法触发，进程已退出")
                            : RunAttemptResult.Success("进程自行退出（未配置日志监控，按退出判定成功）");
                    }
                    else if (judge.IsFailure)
                    {
                        result = RunAttemptResult.Failed(judge.Reason ?? "日志出现失败关键字，任务判定失败");
                    }
                    else if (judge.IsMarker)
                    {
                        result = RunAttemptResult.Success(judge.Reason ?? "日志出现完成标志，脚本正常运行结束");
                        result.NotifyText = judge.NotifyText;
                    }
                    else if (judgeConfigured)
                    {
                        if (scriptMode)
                        {
                            _statusChanged?.Invoke("脚本已退出，触发判断脚本最终判定...");
                            await FinalJudgeOnceAsync().ConfigureAwait(false);
                        }
                        result ??= RunAttemptResult.Failed("进程退出但未检测到完成标志");
                    }
                    else
                    {
                        result = RunAttemptResult.Success("进程自行退出（未配置完成标志，按退出判定成功）");
                    }
                    break;
                }

                if (!judge.IsMarker)
                {
                    bool stallHit = false;
                    string stallReason = "";
                    double stallSeconds = TestHooks.ScaledSeconds(_script.LogStallTimeoutMinutes * 60);
                    if (monitor is null && !string.IsNullOrWhiteSpace(_script.LogPath))
                    {
                        double waitSeconds = (DateTime.Now - attemptStart).TotalSeconds;
                        if (_script.LogStallTimeoutMinutes > 0 && waitSeconds >= stallSeconds)
                        {
                            stallHit = true;
                            stallReason = $"启动后 {_script.LogStallTimeoutMinutes} 分钟未产生日志条目（未找到日志文件）";
                        }
                    }
                    else if (monitor is not null && firstEntryAt is null)
                    {
                        double waitSeconds = (DateTime.Now - attemptStart).TotalSeconds;
                        if (_script.LogStallTimeoutMinutes > 0 && waitSeconds >= stallSeconds)
                        {
                            stallHit = true;
                            stallReason = $"启动后 {_script.LogStallTimeoutMinutes} 分钟未产生日志条目";
                        }
                    }
                    else if (monitor is not null)
                    {
                        double stallSec = (DateTime.Now - monitor.LastWrite).TotalSeconds;
                        if (_script.LogStallTimeoutMinutes > 0 && stallSec >= stallSeconds)
                        {
                            stallHit = true;
                            stallReason = $"日志超过 {_script.LogStallTimeoutMinutes} 分钟无更新";
                        }
                    }
                    if (stallHit)
                    {
                        if (scriptMode)
                        {
                            _statusChanged?.Invoke("日志超时，触发判断脚本最终判定...");
                            await FinalJudgeOnceAsync().ConfigureAwait(false);
                        }
                        result ??= RunAttemptResult.Failed(stallReason);
                        break;
                    }
                }

                if (judge.IsMarker
                    && (DateTime.Now - judge.MarkerSeenAt!.Value).TotalSeconds >= TestHooks.ScaledSeconds(ExitGraceSecondsAfterMarker))
                {
                    KillStartedScript();
                    result = RunAttemptResult.Success(judge.Reason ?? "完成标志已出现，等待退出超时后已终止脚本，判定成功");
                    result.NotifyText = judge.NotifyText;
                    break;
                }

                await Task.Delay(TestHooks.ScaledMs(1000), _token).ConfigureAwait(false);
            }
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

        monitor?.Dispose();
        monitor = null;

        KillStartedScript();
        string resultStatus = result?.Status ?? "failed";
        if (resultStatus == "failed")
        {
            if (!string.IsNullOrWhiteSpace(_script.GameExe))
            {
                Logger.Info($"[{modeText}运行] 脚本「{_script.Name}」任务失败，强制结束游戏进程。");
                SystemActions.KillByName(_script.GameExe, "游戏");
            }
        }
        else if (_script.ForceCloseGame && !string.IsNullOrWhiteSpace(_script.GameExe))
        {
            SystemActions.KillByName(_script.GameExe, "游戏");
        }
        Logger.Info($"[{modeText}运行] 脚本「{_script.Name}」本次尝试清理完成。");
        return result ?? RunAttemptResult.Failed("未知原因：未能取得运行结果");
    }

    /// <summary>
    /// 等待并确认游戏进程启动：每 1 秒轮询进程是否出现，上限为超时时间。
    /// bat/cmd 启动器经 cmd.exe 包装无法按名检测（IsExeRunning 返回 false），直接按已启动放行并等待到超时结束（保持原有等待语义）。
    /// </summary>
    private async Task<bool> WaitForGameProcessAsync(TimeSpan timeout)
    {
        if (SystemActions.IsCommandFile(_script.GameExe))
        {
            await Task.Delay(timeout, _token).ConfigureAwait(false);
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
            await Task.Delay(TestHooks.ScaledMs(1000), _token).ConfigureAwait(false);
        }
    }

    /// <summary>执行一次判断脚本：收集 config/script 文件清单 + 构建输入 JSON + 执行；返回 (status, reason, notifyText, replaceConfigs, error)。</summary>
    private async Task<(string Status, string Reason, string NotifyText, List<string> ReplaceConfigs, string? Error)> RunJudgeOnceAsync()
    {
        string scriptDir = UserConfigManager.ScriptDir(_script.Id, _activeUser?.Name);
        List<JudgeScriptInputFile> files = JudgeScriptRunner.CollectFiles(_script.ConfigPath, scriptDir);
        bool logTruncated = false;
        string logText;
        int logLength = _scriptFullLog.Length - _attemptLogStart;
        if (logLength > JudgeScriptRunner.MaxJudgeLogChars)
        {
            logTruncated = true;
            logText = _scriptFullLog.ToString(_attemptLogStart + logLength - JudgeScriptRunner.MaxJudgeLogChars, JudgeScriptRunner.MaxJudgeLogChars);
        }
        else
        {
            logText = _scriptFullLog.ToString(_attemptLogStart, logLength);
        }
        string inputJson = JudgeScriptRunner.BuildInput(_script, _activeUser, files, scriptDir, logText, logTruncated);
        JudgeScriptResult judge = await JudgeScriptRunner.ExecuteAsync(_script, inputJson, files, _script.ConfigPath, scriptDir, _token).ConfigureAwait(false);
        if (judge.JudgeError is not null)
        {
            return ("", "", "", new List<string>(), judge.JudgeError);
        }
        return (judge.Status, judge.Reason, judge.NotifyText, judge.ReplaceConfigs, null);
    }

    /// <summary>创建日志监控：文件在尝试启动后写过（LastWriteTime ≥ attemptStart，严格无松弛窗口）→ 从头读；否则从末尾读（忽略残留）。</summary>
    private LogMonitor NewMonitor(string resolved, DateTime attemptStart, string modeText, bool rotated = false)
    {
        bool fresh;
        try
        {
            fresh = File.GetLastWriteTime(resolved) >= attemptStart;
        }
        catch (Exception)
        {
            fresh = false;
        }
        var monitor = new LogMonitor(resolved, readFromStart: fresh);
        Logger.Info($"[{modeText}运行] 脚本「{_script.Name}」{(rotated ? "日志轮换，改监控" : "开始监控")}：{resolved}（{(fresh ? "从头" : "末尾")}读取）");
        return monitor;
    }
}
