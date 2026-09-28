using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.Modules.Configuration.Exchange;

/// <summary>
/// 一次脚本运行所拥有的配置交换作用域。调用者只表达生命周期动作，
/// 不再需要记住 original、store-txn、swap-backup 的底层顺序。
/// </summary>
internal sealed class ConfigRunSession
{
    internal enum FinalizationStep
    {
        Sync,
        RestoreReplacements,
        CleanupScriptArea,
        RestoreConfig,
    }

    private readonly string _scriptId;
    private readonly string? _userKey;
    private readonly string _configPath;
    private readonly bool _hasJudgeScript;
    private readonly ConfigSessionRuntimeMetadata? _metadata;
    private readonly IReadOnlyList<string> _extraConfigPaths;
    private readonly string _originExecutionId;
    private readonly string _originRecordId;
    private readonly object _finalizationGate = new();
    private bool _processCleanupConfirmed = true;
    private bool _finalizationCompleted;
    private readonly bool _providerSession;
    private string? _finalizationError;
    internal Func<string?>? RestoreTaskSelections { get; set; }
    internal int OriginAttempt { get; set; }

    public ConfigRunSession(
        string scriptId,
        string? userKey,
        string configPath,
        bool hasJudgeScript,
        ResolvedScriptSpec? resolvedSpec = null,
        string originExecutionId = "",
        string originRecordId = "")
    {
        _scriptId = scriptId;
        _userKey = userKey;
        _configPath = configPath;
        _hasJudgeScript = hasJudgeScript;
        _metadata = resolvedSpec is null ? null : BuildMetadata(resolvedSpec) with { OriginExecutionId = originExecutionId };
        _extraConfigPaths = resolvedSpec?.ExtraConfigPaths ?? Array.Empty<string>();
        _originExecutionId = originExecutionId;
        _originRecordId = originRecordId;
        _providerSession = resolvedSpec?.ProviderPlan is not null;
    }

    public bool IsPrepared { get; private set; }
    internal bool RequiresRestoration => IsPrepared && !_providerSession;

    public string ScriptDir => ConfigPaths.ScriptDir(_scriptId, _userKey);

    public void PrepareScriptArea()
    {
        if (_hasJudgeScript) ConfigWorkAreaService.PrepareScriptDir(_scriptId, _userKey);
    }

    public bool Prepare(out string? error)
    {
        error = null;
        if (_providerSession)
        {
            if (string.IsNullOrWhiteSpace(_userKey) || _metadata is null) { error = "provider requires a Host user binding"; return false; }
            if (File.Exists(ConfigSessionMark.MarkFile(_scriptId, _userKey)) || File.Exists(ConfigSessionMark.BackupMarkFile(_scriptId, _userKey)))
            { error = "provider recovery journal already exists"; return false; }
            new ConfigSessionMark { ScriptId = _scriptId, UserId = _userKey, SessionPhase = "provider_run",
                ConfigPath = _metadata.WritableRoot, ConfigKind = "dir", WorkingDirectory = _metadata.WorkingDirectory,
                WritableRoot = _metadata.WritableRoot, ProfileHash = _metadata.ProfileHash,
                PluginName = _metadata.PluginName, PluginVersion = _metadata.PluginVersion,
                OriginExecutionId = _originExecutionId, ProviderWorkersStopped = false }.Write();
            IsPrepared = true; return true;
        }
        if (string.IsNullOrWhiteSpace(_userKey) || string.IsNullOrWhiteSpace(_configPath))
        {
            return true;
        }
        IsPrepared = ConfigExchangeService.PrepareForRun(_scriptId, _userKey, _configPath, out error, _metadata, _extraConfigPaths);
        return IsPrepared;
    }

    private static ConfigSessionRuntimeMetadata BuildMetadata(ResolvedScriptSpec spec)
    {
        return ConfigSessionMark.FromScript(
            spec.Script,
            spec.ProfileHash,
            spec.PluginVersion,
            spec.ExtraConfigPaths);
    }

    public string? PrepareForRetry()
    {
        if (_providerSession || !IsPrepared || string.IsNullOrWhiteSpace(_userKey))
        {
            return null;
        }
        return ConfigSwapSession.PrepareForRetry(_scriptId, _userKey, _configPath);
    }

    public void SyncToStore(bool firstCheck)
    {
        if (!_providerSession && IsPrepared && !string.IsNullOrWhiteSpace(_userKey))
        {
            ConfigSwapSession.SyncConfigToStore(_scriptId, _userKey, _configPath, firstCheck);
            if (_extraConfigPaths.Count > 0)
            {
                ExtraConfigSync.SyncAllFromSite(_scriptId, _userKey, _extraConfigPaths, SyncPhaseText(firstCheck));
            }
        }
    }

    private static string SyncPhaseText(bool firstCheck) => firstCheck ? "首次检测" : "运行收尾";

    public void ApplyReplacements(List<string> replacements)
    {
        if (RestoreTaskSelections is not null) throw new InvalidOperationException("taskProtocol cannot use legacy replaceConfigs");
        ConfigSwapSession.ApplyConfigReplacements(_scriptId, _userKey, _configPath, replacements);
    }

    /// <summary>进程树未能确认退出时锁住配置收尾，保留现场供恢复，而不是继续覆盖/还原文件。</summary>
    public void MarkProcessCleanupUnconfirmed(string reason)
    {
        _processCleanupConfirmed = false;
        Logger.Error($"[错误] 脚本「{_scriptId}」进程清理未确认，已阻断配置替换/还原：{reason}");
    }

    /// <summary>唯一权威的运行收尾顺序；顺序由测试保护，业务调用者不再手工拼接。</summary>
    internal IReadOnlyList<FinalizationStep> GetFinalizationOrder(bool autoUpdateConfig)
    {
        if (_providerSession) return [];
        return BuildFinalizationOrder(
            autoUpdateConfig && IsPrepared,
            _hasJudgeScript,
            IsPrepared);
    }

    internal static IReadOnlyList<FinalizationStep> BuildFinalizationOrder(bool canSync, bool hasJudgeScript, bool prepared)
    {
        var steps = new List<FinalizationStep>();
        if (canSync)
        {
            steps.Add(FinalizationStep.Sync);
        }
        if (hasJudgeScript)
        {
            steps.Add(FinalizationStep.RestoreReplacements);
            steps.Add(FinalizationStep.CleanupScriptArea);
        }
        if (prepared)
        {
            steps.Add(FinalizationStep.RestoreConfig);
        }
        return steps;
    }

    /// <summary>执行收尾并返回配置交换还原错误；同步失败保留旧快照并继续执行现场还原。</summary>
    public string? FinalizeRun(bool autoUpdateConfig)
    {
        lock (_finalizationGate)
        {
            if (_finalizationCompleted)
            {
                return _finalizationError;
            }

            if (!_processCleanupConfirmed)
            {
                _finalizationError = "脚本进程树未确认退出，已保留配置交换现场供恢复";
                PersistIsolation("process_cleanup_unconfirmed", mayContinueIndependent: false);
                _finalizationCompleted = true;
                return _finalizationError;
            }

            if (_providerSession && IsPrepared && !string.IsNullOrWhiteSpace(_userKey))
            {
                var mark = ConfigSessionMark.TryRead(_scriptId, _userKey);
                if (mark is null || mark.SessionPhase != "provider_run" || mark.OriginExecutionId != _originExecutionId)
                    throw new IOException("provider journal ownership mismatch");
                try
                {
                    mark.ProviderWorkersStopped = true; mark.Write();
                    ConfigSessionMark.Clear(_scriptId, _userKey);
                    if (File.Exists(ConfigSessionMark.MarkFile(_scriptId, _userKey)) || File.Exists(ConfigSessionMark.BackupMarkFile(_scriptId, _userKey)))
                        _finalizationError = "provider journal cleanup pending";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _finalizationError = "provider journal cleanup pending: " + ex.GetType().Name;
                }
                if (_finalizationError is not null)
                    PersistIsolation("config_restore_failed", mayContinueIndependent: true);
                _finalizationCompleted = true; return _finalizationError;
            }

            string? restoreError = null;
            if (RestoreTaskSelections is not null)
            {
                restoreError = RestoreTaskSelections();
                if (restoreError is not null)
                {
                    PersistIsolation("config_restore_failed", mayContinueIndependent: true);
                    _finalizationError = restoreError;
                    _finalizationCompleted = true;
                    return restoreError; // Preserve the user snapshot and the complete recovery site.
                }
            }
            foreach (FinalizationStep step in GetFinalizationOrder(autoUpdateConfig))
            {
                switch (step)
                {
                    case FinalizationStep.Sync:
                        try
                        {
                            SyncToStore(firstCheck: false);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"[配置] 脚本「{_scriptId}」自动更新同步失败：{ex.Message}");
                        }
                        break;
                    case FinalizationStep.RestoreReplacements:
                        ConfigSwapSession.RestoreConfigReplacements(_scriptId, _userKey);
                        break;
                    case FinalizationStep.CleanupScriptArea:
                        ConfigWorkAreaService.CleanupScriptArea(_scriptId, _userKey);
                        break;
                    case FinalizationStep.RestoreConfig:
                        restoreError = RestoreConfig();
                        break;
                }
            }

            _finalizationError = restoreError;
            if (restoreError is not null)
                PersistIsolation("config_restore_failed", mayContinueIndependent: true);
            if (restoreError is null)
            {
                ConfigWorkDirMaintenance.SweepIdleWorkDir(_scriptId, _userKey);
            }
            _finalizationCompleted = true;
            return _finalizationError;
        }
    }

    /// <summary>运行结束后还原：清 config（运行产物），original → config 还原原配置；未准备过或无用户键时无操作。</summary>
    private string? RestoreConfig()
    {
        if (!IsPrepared || string.IsNullOrWhiteSpace(_userKey))
        {
            return null;
        }
        return ConfigExchangeService.RestoreAfterRun(_scriptId, _userKey, _configPath, _extraConfigPaths);
    }

    private void PersistIsolation(string causeCode, bool mayContinueIndependent)
    {
        if (!IsPrepared || string.IsNullOrWhiteSpace(_userKey)) return;
        ConfigSessionMark? mark = ConfigSessionMark.TryRead(_scriptId, _userKey);
        if (mark is null)
        {
            Logger.Error($"[恢复隔离] 脚本「{_scriptId}」缺少会话 journal，范围不可确认。");
            return;
        }
        try
        {
            ConfigSessionRecoveryIsolation isolation = mark.RecoveryIsolation ?? new ConfigSessionRecoveryIsolation();
            isolation.OriginExecutionId = _originExecutionId;
            isolation.OriginRecordId = _originRecordId;
            isolation.OriginAttempt = OriginAttempt;
            isolation.CauseCode = causeCode;
            isolation.ScopeQuality = mayContinueIndependent ? "complete" : "unavailable";
            isolation.MayContinueIndependent = mayContinueIndependent;
            isolation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            mark.RecoveryIsolation = isolation;
            mark.Write();
        }
        catch (Exception ex)
        {
            Logger.Error($"[恢复隔离] journal 写入失败，保留原配置现场：{ex.GetType().Name}: {ex.Message}");
        }
    }
}
