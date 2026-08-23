using NexusPipeline.Utilities;
using NexusPipeline.Services.Configuration;

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

    private readonly ConfigurationTransaction _transaction;
    private readonly bool _hasJudgeScript;
    private readonly object _finalizationGate = new();
    private bool _processCleanupConfirmed = true;
    private bool _finalizationCompleted;
    private string? _finalizationError;

    public ConfigRunSession(string scriptId, string? userName, string configPath, bool hasJudgeScript)
    {
        _transaction = new ConfigurationTransaction(scriptId, userName, configPath);
        _hasJudgeScript = hasJudgeScript;
    }

    public bool IsPrepared => _transaction.IsPrepared;

    public bool ProcessCleanupConfirmed => _processCleanupConfirmed;

    public string ScriptDir => _transaction.ScriptDir;

    public void PrepareScriptArea()
    {
        if (_hasJudgeScript) _transaction.PrepareScriptArea();
    }

    public bool Prepare(out string? error)
    {
        error = null;
        return _transaction.Begin(out error);
    }

    public string? PrepareForRetry()
    {
        return _transaction.PrepareRetry();
    }

    public void SyncToStore(bool firstCheck)
    {
        _transaction.SyncToStore(firstCheck);
    }

    public void ApplyReplacements(List<string> replacements)
    {
        _transaction.ApplyReplacements(replacements);
    }

    /// <summary>进程树未能确认退出时锁住配置收尾，保留现场供恢复，而不是继续覆盖/还原文件。</summary>
    public void MarkProcessCleanupUnconfirmed(string reason)
    {
        _processCleanupConfirmed = false;
        Logger.Error($"[错误] 脚本「{_transaction.ScriptId}」进程清理未确认，已阻断配置替换/还原：{reason}");
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
                            Logger.Warn($"[配置] 脚本「{_transaction.ScriptId}」自动更新同步失败：{ex.Message}");
                        }
                        break;
                    case FinalizationStep.RestoreReplacements:
                        _transaction.RestoreReplacements();
                        break;
                    case FinalizationStep.CleanupScriptArea:
                        _transaction.CleanupScriptArea();
                        break;
                    case FinalizationStep.RestoreConfig:
                        restoreError = _transaction.Restore();
                        break;
                }
            }

            _finalizationError = restoreError;
            _finalizationCompleted = true;
            return _finalizationError;
        }
    }
}
