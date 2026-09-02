using NexusPipeline.Utilities;
using NexusPipeline.Services.Execution;
using NexusPipeline.Models;

namespace NexusPipeline.Services;

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
    private readonly object _finalizationGate = new();
    private bool _processCleanupConfirmed = true;
    private bool _finalizationCompleted;
    private string? _finalizationError;

    public ConfigRunSession(
        string scriptId,
        string? userKey,
        string configPath,
        bool hasJudgeScript,
        ResolvedScriptSpec? resolvedSpec = null)
    {
        _scriptId = scriptId;
        _userKey = userKey;
        _configPath = configPath;
        _hasJudgeScript = hasJudgeScript;
        _metadata = resolvedSpec is null ? null : BuildMetadata(resolvedSpec);
    }

    public bool IsPrepared { get; private set; }

    public string ScriptDir => UserConfigManager.ScriptDir(_scriptId, _userKey);

    public void PrepareScriptArea()
    {
        if (_hasJudgeScript) UserConfigManager.PrepareScriptDir(_scriptId, _userKey);
    }

    public bool Prepare(out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(_userKey) || string.IsNullOrWhiteSpace(_configPath))
        {
            return true;
        }
        IsPrepared = UserConfigManager.PrepareForRun(_scriptId, _userKey, _configPath, out error, _metadata);
        return IsPrepared;
    }

    private static ConfigSessionRuntimeMetadata BuildMetadata(ResolvedScriptSpec spec)
    {
        return ConfigSessionMark.FromScript(spec.Script, spec.ProfileHash, spec.PluginVersion);
    }

    public string? PrepareForRetry()
    {
        if (!IsPrepared || string.IsNullOrWhiteSpace(_userKey))
        {
            return null;
        }
        return ConfigSwapSession.PrepareForRetry(_scriptId, _userKey, _configPath);
    }

    public void SyncToStore(bool firstCheck)
    {
        if (IsPrepared && !string.IsNullOrWhiteSpace(_userKey))
        {
            ConfigSwapSession.SyncConfigToStore(_scriptId, _userKey, _configPath, firstCheck);
        }
    }

    public void ApplyReplacements(List<string> replacements)
    {
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

    /// <summary>执行收尾并返回配置交换还原错误；同步失败由现有门面记录警告，不阻断后续还原。</summary>
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
                _finalizationCompleted = true;
                return _finalizationError;
            }

            string? restoreError = null;
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
                        UserConfigManager.CleanupScriptArea(_scriptId, _userKey);
                        break;
                    case FinalizationStep.RestoreConfig:
                        restoreError = RestoreConfig();
                        break;
                }
            }

            _finalizationError = restoreError;
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
        return UserConfigManager.RestoreAfterRun(_scriptId, _userKey, _configPath);
    }
}
