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

/// <summary>一次运行的应用层协调器；状态由基类 RunSession 持有。</summary>
internal sealed partial class ExecutionCoordinator : RunSession
{
    private readonly IUserRepository _users;

    private readonly ResolvedScriptSpec? _resolvedSpec;

    private readonly string _queueHasFollowingWork;
    private readonly string _queueNextTargetRelation;
    private readonly string _queueNextLaunchOwner;

    private readonly Action<ExecutionPreviewTarget>? _previewTargetChanged;

    private readonly RunScreenshotStore _screenshotStore;

    private readonly AttemptScreenshotCapture _screenshotCapture;

    private readonly OutboundHttpClientProvider? _http;

    private readonly IEmulatorSupportProviderResolver _emulatorSupportProviders;
    private readonly IPluginExecutionProviderResolver? _executionProviders;
    private ProviderTaskProjection? _providerProjection;
    private string _providerRecordId = "";

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
    internal Action<CancellationMilestone, string?>? CancellationMilestoneChanged { get; set; }
    internal string OriginExecutionId { get; set; } = "";

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
        Action<int, int>? attemptStarted = null,
        string queueNextTargetRelation = "unknown",
        string queueNextLaunchOwner = "unknown",
        IPluginExecutionProviderResolver? executionProviders = null)
        : base(script, mode, queueId, queueName, userName, token, resolvedUser, attemptChanged, statusChanged, logLine)
    {
        _users = users;
        _executionProviders = executionProviders;
        _resolvedSpec = resolvedSpec;
        _queueHasFollowingWork = queueHasFollowingWork is "yes" or "no" ? queueHasFollowingWork : "unknown";
        _queueNextTargetRelation = queueNextTargetRelation is "same" or "different" ? queueNextTargetRelation : "unknown";
        _queueNextLaunchOwner = queueNextLaunchOwner is "host" or "upstream" or "already_running"
            ? queueNextLaunchOwner : "unknown";
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
            _queueId, _queueHasFollowingWork, ready, _queueNextTargetRelation, _queueNextLaunchOwner);
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
        bool? gameTargetReady = null,
        string queueNextTargetRelation = "unknown",
        string queueNextLaunchOwner = "unknown")
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
            new TaskQueueContext(queueKind, following, queueNextTargetRelation, queueNextLaunchOwner),
            new TaskCleanupContext(true, hostWillCloseGame, "none"),
            new TaskEffectiveLaunch(script.Id, script.LaunchGame, null, null, null),
            new TaskLogSourceContext(
                ShouldPublishConsoleData(script.LogPath) ? "stdout" : "file",
                true))
        {
            ConfigInputName = resolvedSpec?.ConfigInputName,
            ConfigInputValue = resolvedSpec?.ConfigInputValue,
        };
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
}
