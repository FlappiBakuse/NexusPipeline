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
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Windows;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Modules.Configuration.Paths;

namespace NexusPipeline.Modules.Execution;

/// <summary>一次运行的应用层协调器；状态由基类 RunSession 持有。</summary>
internal sealed class ExecutionCoordinator : RunSession
{
    /// <summary>成功判定后等待脚本自行退出的宽限秒数（NEXUS_TIME_SCALE 加速时按比例缩放）。</summary>

    private readonly IUserRepository _users;

    private readonly ResolvedScriptSpec? _resolvedSpec;

    private readonly string _queueHasFollowingWork;

    private readonly Action<ExecutionPreviewTarget>? _previewTargetChanged;

    private readonly RunScreenshotStore _screenshotStore;

    private readonly AttemptScreenshotCapture _screenshotCapture;

    private readonly OutboundHttpClientProvider? _http;

    private readonly IEmulatorSupportProviderResolver _emulatorSupportProviders;

    private int? _gameProcessId;

    private readonly object _gameFrontSync = new();

    private int? _frontedGameProcessId;

    private int? _frontingGameProcessId;

    private Task<bool>? _frontingGameTask;

    private ExecutionPreviewTarget? _currentPreviewTarget;

    private IEmulatorDriver? _emulatorDriver;

    private CancellationTokenSource? _budgetExpiryCts;

    private CancellationTokenSource? _operationCts;

    private RunBudgetWatchdog? _budgetWatchdog;

    private readonly UserHookRunner _userHookRunner;

    private volatile bool _budgetExpired;
    internal Action<System.Text.Json.Nodes.JsonObject>? TaskReportChanged { get; set; }
    internal Action<RunRecord>? TaskCheckpointChanged { get; set; }

    private CancellationToken OperationToken => _operationCts?.Token ?? _token;

    private readonly string? _recordId;

    private readonly Action<string, LogLevel, int>? _attemptLogLine;
    private readonly Action<int, int>? _attemptStarted;

    public ExecutionCoordinator(ScriptInstance script, string mode, string queueId, string queueName, string? userName, CancellationToken token,
        Action<int, int>? attemptChanged,
        Action<string>? statusChanged,
        Action<string, LogLevel>? logLine,
         Action<ExecutionPreviewTarget>? previewTargetChanged,
         IUserRepository users,
        IEmulatorSupportProviderResolver emulatorSupportProviders,
        ResolvedScriptUser? resolvedUser = null,
        ResolvedScriptSpec? resolvedSpec = null,
        OutboundHttpClientProvider? http = null,
        string queueHasFollowingWork = "unknown",
        string? recordId = null,
        Action<string, LogLevel, int>? attemptLogLine = null,
        Action<int, int>? attemptStarted = null)
        : base(script, mode, queueId, queueName, userName, token, resolvedUser, attemptChanged, statusChanged, logLine)
    {
        _users = users;
        _resolvedSpec = resolvedSpec;
        _queueHasFollowingWork = queueHasFollowingWork is "yes" or "no" ? queueHasFollowingWork : "unknown";
        _recordId = recordId;
        _attemptLogLine = attemptLogLine;
        _attemptStarted = attemptStarted;
        _http = http;
        _emulatorSupportProviders = emulatorSupportProviders ?? throw new ArgumentNullException(nameof(emulatorSupportProviders));
        _previewTargetChanged = previewTargetChanged;
        _screenshotCapture = new AttemptScreenshotCapture(
            _script,
            () => _currentPreviewTarget,
            () => _gameProcessId,
            ResolveCurrentPcProcessId,
            () => _emulatorDriver);
        _screenshotStore = new RunScreenshotStore(_screenshotCapture.CaptureAsync);
        _userHookRunner = new UserHookRunner(
            _script,
            _mode,
            _statusChanged,
            _logLine,
            RemainingRunSeconds,
            () => _budgetExpired || _budget?.IsExpired == true,
            message => _configRun?.MarkProcessCleanupUnconfirmed(message),
            AppendScriptLog,
            attemptLogLine);
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

    private TaskExecutionContext CreateTaskExecutionContext(string userId, string trigger)
    {
        // Only the MXU PC preflight uses this live fact. A configured executable
        // or a process with no visible window is not an already usable target.
        bool? ready = _script.GameMode == "pc" && !_script.LaunchGame
            && _script.PluginType is "maaend" or "maas"
            ? FindGameProcessId(null) is > 0 : null;
        return CreateTaskExecutionContext(_script, _resolvedSpec, userId, trigger,
            _queueId, _queueHasFollowingWork, ready);
    }

    /// <summary>
    /// Builds the immutable execution facts shared by real runs and read-only task previews.
    /// Keeping this in one place is important because configuration target checks compare
    /// plugin-owned paths with the bound ScriptInstance.GameExe.
    /// </summary>
    internal static TaskExecutionContext CreateTaskExecutionContext(
        ScriptInstance script,
        ResolvedScriptSpec? resolvedSpec,
        string userId,
        string trigger,
        string? queueId = null,
        string queueHasFollowingWork = "unknown",
        bool? gameTargetReady = null)
    {
        string mode = EmulatorSupport.IsEmulator(script)
            ? "emulator"
            : script.GameMode is "pc" or "cloud" ? script.GameMode : "pc";
        bool hasGameTarget = !string.IsNullOrWhiteSpace(script.GameExe);
        string targetKind = !hasGameTarget ? "none" : mode == "emulator" ? "adb_endpoint" : "executable";
        string launchOwner = !script.LaunchGame
            ? "already_running"
            : ShouldHostLaunchGame(script, resolvedSpec) ? "host" : "upstream";
        string queueKind = string.IsNullOrWhiteSpace(queueId) ? "standalone" : "queue";
        string following = string.IsNullOrWhiteSpace(queueId)
            ? "no"
            : queueHasFollowingWork is "yes" or "no" ? queueHasFollowingWork : "unknown";
        bool hostWillCloseGame = script.ForceCloseGame && !(resolvedSpec?.SelfManagedPcLaunch == true);
        return new TaskExecutionContext(
            userId,
            script.Id,
            userId + ":" + script.Id,
            trigger,
            mode,
            launchOwner,
            new TaskGameTarget(targetKind, hasGameTarget ? script.GameExe : null, null, gameTargetReady),
            new TaskQueueContext(queueKind, following),
            new TaskCleanupContext(true, hostWillCloseGame, "none"),
            new TaskEffectiveLaunch(script.Id, script.LaunchGame, null, null, null),
            new TaskLogSourceContext(
                ShouldPublishConsoleData(script.LogPath) ? "stdout" : "file",
                true));
    }

    /// <summary>按本次实际解析出的有效判定配置决定是否需要最近 PC 帧缓存。</summary>
    internal static bool NeedsRecentPcScreenshotCache(ScriptInstance script, ResolvedScriptSpec? spec)
    {
        if (EmulatorSupport.IsEmulator(script))
        {
            return false;
        }
        // 专项脚本的 spec 是 frozen profile 的有效事实；通用脚本由解析后的 Script 字段表达。
        return spec?.JudgeScript.Enabled == true || script.HasJudgeScript() || script.HasKeywords();
    }

    public async Task<RunRecord> RunAsync()
    {
        _budget = new RunBudget(_script.TotalTimeoutMinutes, DateTime.Now);
        var record = new RunRecord
        {
            Id = _recordId ?? Guid.NewGuid().ToString("N"),
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
        if (_resolvedSpec?.TaskProtocol is not null)
            TaskProtocolRun = new TaskProtocolRun(_resolvedSpec, record.Id, record.UserId,
                executionContext: CreateTaskExecutionContext(record.UserId, "pre_launch")) { Changed = report =>
            {
                var checkpoint = record.Clone(); checkpoint.TaskReport = report;
                TaskCheckpointChanged?.Invoke(checkpoint);
                TaskReportChanged?.Invoke(report);
            } };

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
                _script.HasJudgeScript() && TaskProtocolRun is null,
                _resolvedSpec);
            if (TaskProtocolRun is not null) _configRun.RestoreTaskSelections = TaskProtocolRun.Restore;
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

            if (TaskProtocolRun is not null)
            {
                try
                {
                    await TaskProtocolRun.PreflightAsync(OperationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    record.Status = "cancelled";
                    record.EndTime = DateTime.Now;
                    record.ResultDetail = "任务发现已取消";
                    record.ResultCode = "run.cancelled";
                    return record;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[专项任务] 配置发现失败：{ex.GetType().Name}");
                    record.Status = "failed";
                    record.EndTime = DateTime.Now;
                    record.ResultDetail = "无法建立可信任务计划";
                    record.ResultCode = "tasks.discovery_failed";
                    return record;
                }
                if (TaskProtocolRun.IsAdmissionBlocked)
                {
                    record.Status = "blocked";
                    record.EndTime = DateTime.Now;
                    record.ResultDetail = "配置检查未通过，未启动脚本或游戏";
                    record.ResultCode = "tasks.admission_blocked";
                    record.TaskReport = TaskProtocolRun.Snapshot();
                    return record;
                }
            }

            for (int attemptNo = 1; attemptNo <= maxAttempts; attemptNo++)
            {
                _attemptStarted?.Invoke(attemptNo, maxAttempts);
                _attemptChanged?.Invoke(attemptNo, maxAttempts);
                if (attemptNo > 1 && _configRun.IsPrepared && TaskProtocolRun is null)
                {
                    string? retryError = _configRun.PrepareForRetry();
                    if (retryError is not null)
                    {
                        _attemptLogStart = Results.Length;
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
                _attemptLogStart = Results.Length;
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

                if (_token.IsCancellationRequested && result.Status != "cancelled")
                    result = RunAttemptResult.Cancelled("运行已取消");

                if (mainExecuted && result.Status != "cancelled" && TaskProtocolRun?.IsAdmissionBlocked != true
                    && user is not null && !string.IsNullOrWhiteSpace(user.Binding.PostRunScript)
                    && (TaskProtocolRun is not null ? !user.Binding.PostRunOnFinalOnly : AttemptLifecycle.ShouldRunPostRun(
                        user.Binding.PostRunOnFinalOnly,
                        attemptNo,
                        retryPolicy,
                        result)))
                {
                    RunAttemptResult? postResult = await RunUserScriptCoreAsync(user!.Binding.PostRunScript, "任务后", attempt, OperationToken).ConfigureAwait(false);
                    if (postResult is not null)
                    {
                        result = RunAttemptResult.MergePostRun(result, postResult);
                    }
                }

                bool taskRetry = false;
                bool retryAdmissionBlocked = TaskProtocolRun?.IsAdmissionBlocked == true
                    && !TaskProtocolRun.AdmissionBlockedBeforeAttempt
                    && result.ReasonCode == "tasks.admission_blocked";
                if (TaskProtocolRun is not null)
                {
                    result = TaskProtocolRun.Finish(result, attemptNo);
                    if (!result.IsFatal && result.Status is not ("success" or "skipped" or "cancelled"))
                    {
                        try
                        {
                            taskRetry = await TaskProtocolRun.PrepareRetryAsync(maxAttempts, _token.IsCancellationRequested,
                                _budgetExpired || _budget.IsExpired, OperationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"[专项任务] 安全重试已停止：{ex.GetType().Name}");
                            taskRetry = false;
                        }
                    }
                    if (!taskRetry && mainExecuted && result.Status != "cancelled" && !TaskProtocolRun.IsAdmissionBlocked
                        && user?.Binding.PostRunOnFinalOnly == true && !string.IsNullOrWhiteSpace(user.Binding.PostRunScript))
                    {
                        var postResult = await RunUserScriptCoreAsync(user.Binding.PostRunScript, "任务后", attempt, OperationToken).ConfigureAwait(false);
                        if (postResult is not null)
                        {
                            result = RunAttemptResult.MergePostRun(result, postResult);
                            TaskProtocolRun.SetFinalLifecycleFailure(result.Status == "cancelled");
                        }
                    }
                }
                if (TaskProtocolRun?.IsAdmissionBlocked == true && TaskProtocolRun.AdmissionBlockedBeforeAttempt)
                {
                    if (runPreRun)
                    {
                        // Keep the hook's real execution and log while making it
                        // clear that no protocol/main attempt was admitted.
                        attempt.EndTime = DateTime.Now;
                        attempt.Status = "blocked";
                        attempt.Reason = "配置检查未通过，未启动脚本或游戏";
                        attempt.ReasonCode = "tasks.admission_blocked";
                        AppendScriptLog($"===== 第 {attemptNo}/{maxAttempts} 次尝试 停止：{attempt.Reason} =====");
                    }
                    else record.AttemptDetails.Remove(attempt);
                    record.Attempts = 0;
                    record.Status = "blocked";
                    record.EndTime = DateTime.Now;
                    record.ResultDetail = "配置检查未通过，未启动脚本或游戏";
                    record.ResultCode = "tasks.admission_blocked";
                    Results.CompleteAttempt();
                    record.TaskReport = TaskProtocolRun.Snapshot();
                    break;
                }
                if (retryAdmissionBlocked)
                {
                    // The pre-run hook may have executed and its log belongs to
                    // this entry, but BeginAsync rejected the retry before a
                    // second protocol/main attempt started.
                    attempt.EndTime = DateTime.Now;
                    attempt.Status = "blocked";
                    attempt.Reason = "配置检查未通过，未启动脚本或游戏";
                    attempt.ReasonCode = "tasks.admission_blocked";
                    record.Attempts = attemptNo - 1;
                    record.Status = result.Status;
                    record.EndTime = attempt.EndTime;
                    record.ResultDetail = result.Reason;
                    record.ResultCode = result.ReasonCode;
                    record.ResultArgs = new(result.ReasonArgs, StringComparer.Ordinal);
                    AppendScriptLog($"===== 第 {attemptNo}/{maxAttempts} 次尝试 停止：{attempt.Reason} =====");
                    Results.CompleteAttempt();
                    record.TaskReport = TaskProtocolRun!.Snapshot();
                    break;
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

                if (TaskProtocolRun is not null)
                {
                    record.Status = result.Status;
                    record.EndTime = DateTime.Now;
                    record.ResultDetail = result.Reason;
                    record.TaskReport = TaskProtocolRun.Snapshot();
                    if (taskRetry) continue;
                    break;
                }
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
                if (_token.IsCancellationRequested) _statusChanged?.Invoke("正在恢复配置");
                string? restoreError = _configRun.FinalizeRun(_script.AutoUpdateConfig);
                if (restoreError is not null)
                {
                    if (TaskProtocolRun is not null)
                    {
                        record.Status = "failed"; record.ResultCode = "tasks.recovery_conflict";
                        TaskProtocolRun.SetFinalLifecycleFailure(false);
                    }
                    string msg = $"（警告：配置还原失败，现场已保留，详见日志）";
                    record.ResultDetail += msg;
                    Logger.Error($"[错误] 脚本「{_script.Name}」用户「{user?.UserName ?? _userName ?? ""}」配置还原失败：{restoreError}");
                }
                if (_token.IsCancellationRequested)
                    _statusChanged?.Invoke(restoreError is null ? "配置恢复完成" : "配置恢复失败，现场已保留");
            }
            if (TaskProtocolRun is not null)
            {
                if (record.Status is "failed" or "cancelled" && TaskProtocolRun.Snapshot()?["lifecycleOutcome"]?.GetValue<string>() == "running")
                    TaskProtocolRun.SetFinalLifecycleFailure(record.Status == "cancelled");
                record.TaskReport = TaskProtocolRun.Snapshot();
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
            _statusChanged);
        RunAttemptResult? gameLaunchError = await gameLauncher.LaunchAsync().ConfigureAwait(false);
        if (gameLaunchError is not null)
        {
            return await FinishEarlyAsync(gameLaunchError).ConfigureAwait(false);
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
            : (string.IsNullOrWhiteSpace(_script.GameExe) ? null : Path.GetFileNameWithoutExtension(_script.GameExe));
        if (SystemActions.IsExeRunning(launchExe))
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
        if (_token.IsCancellationRequested) _statusChanged?.Invoke("正在停止脚本");
        bool stopped = KillScriptAndConfirm();
        if (_token.IsCancellationRequested && stopped) _statusChanged?.Invoke("脚本已停止，正在收拢后台任务");
        await workers.StopAsync().ConfigureAwait(false);
        if (_token.IsCancellationRequested) _statusChanged?.Invoke("后台任务已收拢");

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

    private int? FindGameProcessId(int? preferredProcessId, AttemptProcessSnapshot? processSnapshot = null)
    {
        string processName = Path.GetFileNameWithoutExtension(_script.GameExe ?? "");
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        IReadOnlyList<int> configuredProcessIds = FindConfiguredGameProcessIds(processName, processSnapshot);
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
