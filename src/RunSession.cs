using System.Diagnostics;
using System.Text;

namespace NexusPipeline;

internal class RunAttemptResult
{
    public string Status { get; set; } = "";

    public string Reason { get; set; } = "";

    public bool IsFatal { get; set; }

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

    public string ScriptLog
    {
        get
        {
            if (_scriptLogTruncated)
            {
                _scriptFullLog.AppendLine("（脚本日志超过 20MB，已截断尾部）");
            }
            return _scriptFullLog.ToString();
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
            return;
        }
        _scriptFullLog.AppendLine(line);
    }

    public async Task<RunRecord> RunAsync()
    {
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
                attempt.OutputFile = ConsoleLog.FileFor(DateTime.Now);
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
                        result = postResult;
                    }
                }

                attempt.EndTime = DateTime.Now;
                attempt.Status = result.Status;
                attempt.Reason = result.Reason;
                record.Attempts = attemptNo;
                AppendScriptLog($"===== 第 {attemptNo}/{maxAttempts} 次尝试 结束：{result.Status}（{result.Reason}） =====");
                Logger.Info($"第 {attemptNo} 次尝试结束：{result.Status}（{result.Reason}）");

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
        ConsoleLog.WriteSeparator($"脚本「{_script.Name}」第 {attempt.Number} 次尝试 {role}脚本 输出开始（PID {process.Id}）");

        void OnConsoleData(string? data)
        {
            if (string.IsNullOrWhiteSpace(data))
            {
                return;
            }
            _logLine?.Invoke(data);
            ConsoleLog.Write(data);
        }

        process.OutputDataReceived += (_, e) => OnConsoleData(e.Data);
        process.ErrorDataReceived += (_, e) => OnConsoleData(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            using var timeoutCts = new CancellationTokenSource();
            if (_script.TotalTimeoutMinutes > 0)
            {
                timeoutCts.CancelAfter(TimeSpan.FromMinutes(_script.TotalTimeoutMinutes));
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
        finally
        {
            ConsoleLog.WriteSeparator($"脚本「{_script.Name}」第 {attempt.Number} 次尝试 {role}脚本 输出结束");
        }
    }

    private async Task<RunAttemptResult> RunAttemptAsync(RunAttempt attempt)
    {
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
                    _ = SystemActions.StartWithOutputDrain(gamePsi, disposeWhenExited: true);
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

        Process? process = null;
        void KillStartedScript()
        {
            if (process is null)
            {
                return;
            }
            SystemActions.KillTree(process.Id);
            if (SystemActions.IsExeRunning(_script.MainExe))
            {
                Logger.Info($"[提示] 脚本「{_script.Name}」主进程已退出但检测到同名进程仍在运行（自重启产物），按进程名强制结束。");
                SystemActions.KillByName(_script.MainExe, "脚本");
            }
        }

        if (SystemActions.IsExeRunning(_script.MainExe))
        {
            Logger.Info($"[{modeText}运行] 脚本「{_script.Name}」检测到已在运行，直接监控其日志（不重复启动）。");
            _statusChanged?.Invoke("检测到脚本已在运行，直接监控其日志...");
        }
        else
        {
            var psi = SystemActions.BuildScriptStartInfo(_script.MainExe, workingDir, TextRules.SplitArgs(_script.Args), noWindow: true, redirect: true);
            try
            {
                process = Process.Start(psi);
            }
            catch (Exception ex)
            {
                return RunAttemptResult.Failed($"脚本启动失败：{ex.Message}");
            }
            if (process is null)
            {
                return RunAttemptResult.Failed("脚本启动失败：未能创建进程");
            }
            _statusChanged?.Invoke($"脚本已启动（PID {process.Id}）");
            Logger.Info($"[{modeText}运行] 脚本「{_script.Name}」已启动：{_script.MainExe}（PID {process.Id}）");
            ConsoleLog.WriteSeparator($"脚本「{_script.Name}」第 {attempt.Number} 次尝试 控制台输出开始（PID {process.Id}）");
        }

        var outputTail = new StringBuilder();

        void OnConsoleData(string? data)
        {
            if (string.IsNullOrWhiteSpace(data))
            {
                return;
            }
            outputTail.AppendLine(data);
            if (outputTail.Length > 8192)
            {
                outputTail.Remove(0, 4096);
            }
            _logLine?.Invoke(data);
            ConsoleLog.Write(data);
        }

        if (process is not null)
        {
            process.OutputDataReceived += (_, e) => OnConsoleData(e.Data);
            process.ErrorDataReceived += (_, e) => OnConsoleData(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        string? resolvedBeforeStart = string.IsNullOrWhiteSpace(_script.LogPath) ? null : LogPattern.ResolveFile(_script.LogPath);
        LogMonitor? monitor = resolvedBeforeStart is null ? null : new LogMonitor(resolvedBeforeStart, readFromStart: false);
        List<string> markers = _script.MarkerList();
        var logTail = new List<string>();
        DateTime attemptStart = DateTime.Now;
        DateTime? firstEntryAt = null;
        DateTime? markerSeenAt = null;
        RunAttemptResult? result = null;

        try
        {
            while (result is null)
            {
                _token.ThrowIfCancellationRequested();

                if (_script.TotalTimeoutMinutes > 0
                    && (DateTime.Now - attemptStart).TotalMinutes >= _script.TotalTimeoutMinutes)
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
                                if (File.GetCreationTimeUtc(resolved).Ticks != monitor.FileStamp)
                                {
                                    monitor.ReopenFromStart();
                                    Logger.Info($"[{modeText}运行] 脚本「{_script.Name}」日志文件被重建，已重新从头读取：{resolved}");
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
                        logTail.Add(line);
                        if (logTail.Count > 50)
                        {
                            logTail.RemoveAt(0);
                        }
                        _logLine?.Invoke(line);
                        AppendScriptLog(line);
                        if (markerSeenAt is null && TextRules.LineHasCompletionMarker(line, markers))
                        {
                            markerSeenAt = DateTime.Now;
                            _statusChanged?.Invoke("已检测到完成标志，等待脚本退出...");
                            Logger.Info($"[{modeText}运行] 脚本「{_script.Name}」日志出现完成标志。");
                        }
                    }
                }

                bool scriptExited = process is null
                    ? !SystemActions.IsExeRunning(_script.MainExe)
                    : process.HasExited && !SystemActions.IsExeRunning(_script.MainExe);
                if (scriptExited)
                {
                    if (monitor is null && !string.IsNullOrWhiteSpace(_script.LogPath))
                    {
                        result = RunAttemptResult.Failed("已配置日志路径但未找到日志文件，进程退出且未检测到完成标志");
                    }
                    else if (monitor is null)
                    {
                        result = RunAttemptResult.Success("进程自行退出（未配置日志监控，按退出判定成功）");
                    }
                    else if (markerSeenAt is not null)
                    {
                        result = RunAttemptResult.Success("日志出现完成标志，脚本正常运行结束");
                    }
                    else
                    {
                        result = RunAttemptResult.Failed("进程退出但未检测到完成标志");
                    }
                    break;
                }

                if (markerSeenAt is null)
                {
                    if (monitor is null && !string.IsNullOrWhiteSpace(_script.LogPath))
                    {
                        double waitMinutes = (DateTime.Now - attemptStart).TotalMinutes;
                        if (_script.LogStallTimeoutMinutes > 0 && waitMinutes >= _script.LogStallTimeoutMinutes)
                        {
                            result = RunAttemptResult.Failed($"启动后 {_script.LogStallTimeoutMinutes} 分钟未产生日志条目（未找到日志文件）");
                            break;
                        }
                    }
                    else if (monitor is not null && firstEntryAt is null)
                    {
                        double waitMinutes = (DateTime.Now - attemptStart).TotalMinutes;
                        if (_script.LogStallTimeoutMinutes > 0 && waitMinutes >= _script.LogStallTimeoutMinutes)
                        {
                            result = RunAttemptResult.Failed($"启动后 {_script.LogStallTimeoutMinutes} 分钟未产生日志条目");
                            break;
                        }
                    }
                    else if (monitor is not null)
                    {
                        double stallMinutes = (DateTime.Now - monitor.LastWrite).TotalMinutes;
                        if (_script.LogStallTimeoutMinutes > 0 && stallMinutes >= _script.LogStallTimeoutMinutes)
                        {
                            result = RunAttemptResult.Failed($"日志超过 {_script.LogStallTimeoutMinutes} 分钟无更新");
                            break;
                        }
                    }
                }

                if (markerSeenAt is not null
                    && (DateTime.Now - markerSeenAt.Value).TotalSeconds >= ExitGraceSecondsAfterMarker)
                {
                    KillStartedScript();
                    result = RunAttemptResult.Success("完成标志已出现，等待退出超时后已终止脚本，判定成功");
                    break;
                }

                await Task.Delay(1000, _token).ConfigureAwait(false);
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

        ConsoleLog.WriteSeparator($"脚本「{_script.Name}」第 {attempt.Number} 次尝试 控制台输出结束：{result?.Status}（{result?.Reason}）");
        monitor?.Dispose();
        monitor = null;
        attempt.OutputTail = TextRules.TakeTail(outputTail.ToString(), 50);
        attempt.LogTail = new List<string>(logTail);

        KillStartedScript();
        if (_script.ForceCloseGame && _script.LaunchGame && !string.IsNullOrWhiteSpace(_script.GameExe))
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
            await Task.Delay(1000, _token).ConfigureAwait(false);
        }
    }

    /// <summary>创建日志监控：文件在尝试启动后产生（新文件/轮换）→ 从头读；启动前已存在 → 从末尾读（忽略已有日志）。</summary>
    private LogMonitor NewMonitor(string resolved, DateTime attemptStart, string modeText, bool rotated = false)    {
        bool fresh;
        try
        {
            fresh = File.GetLastWriteTime(resolved) >= attemptStart.AddSeconds(-5);
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
