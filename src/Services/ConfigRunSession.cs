using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>
/// 一次脚本运行所拥有的配置交换作用域。调用者只表达生命周期动作，
/// 不再需要记住 original/retry-store/swap-backup 的底层顺序。
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
    private readonly string? _userName;
    private readonly string _configPath;
    private readonly bool _hasJudgeScript;

    public ConfigRunSession(string scriptId, string? userName, string configPath, bool hasJudgeScript)
    {
        _scriptId = scriptId;
        _userName = userName;
        _configPath = configPath;
        _hasJudgeScript = hasJudgeScript;
    }

    public bool IsPrepared { get; private set; }

    public string ScriptDir => UserConfigManager.ScriptDir(_scriptId, _userName);

    public void PrepareScriptArea()
    {
        if (_hasJudgeScript)
        {
            UserConfigManager.PrepareScriptDir(_scriptId, _userName);
        }
    }

    public bool Prepare(out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(_userName) || string.IsNullOrWhiteSpace(_configPath))
        {
            return true;
        }
        IsPrepared = UserConfigManager.PrepareForRun(_scriptId, _userName, _configPath, out error);
        return IsPrepared;
    }

    public string? PrepareForRetry()
    {
        return IsPrepared && !string.IsNullOrWhiteSpace(_userName)
            ? UserConfigManager.PrepareForRetry(_scriptId, _userName, _configPath)
            : null;
    }

    public void SyncToStore(bool firstCheck)
    {
        if (IsPrepared && !string.IsNullOrWhiteSpace(_userName))
        {
            UserConfigManager.SyncConfigToStore(_scriptId, _userName, _configPath, firstCheck);
        }
    }

    public void ApplyReplacements(List<string> replacements)
    {
        UserConfigManager.ApplyConfigReplacements(_scriptId, _userName, _configPath, replacements);
    }

    /// <summary>唯一权威的运行收尾顺序；顺序由测试保护，业务调用者不再手工拼接。</summary>
    internal IReadOnlyList<FinalizationStep> GetFinalizationOrder(bool autoUpdateConfig)
    {
        return BuildFinalizationOrder(
            autoUpdateConfig && IsPrepared && !string.IsNullOrWhiteSpace(_userName),
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
                    UserConfigManager.RestoreConfigReplacements(_scriptId, _userName);
                    break;
                case FinalizationStep.CleanupScriptArea:
                    UserConfigManager.CleanupScriptArea(_scriptId, _userName);
                    break;
                case FinalizationStep.RestoreConfig:
                    restoreError = UserConfigManager.RestoreAfterRun(_scriptId, _userName!, _configPath);
                    break;
            }
        }
        return restoreError;
    }
}
