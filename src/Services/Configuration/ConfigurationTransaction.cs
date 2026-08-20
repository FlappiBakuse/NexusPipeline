using NexusPipeline.Services;

namespace NexusPipeline.Services.Configuration;

/// <summary>
/// 配置运行事务边界：封装 prepare、retry、sync、replace 和 rollback 原语。
/// 收尾顺序由 ConfigRunSession 负责，避免把事务状态与运行编排重新耦合。
/// </summary>
internal sealed class ConfigurationTransaction
{
    private readonly string _scriptId;
    private readonly string? _userName;
    private readonly string _configPath;

    public ConfigurationTransaction(string scriptId, string? userName, string configPath)
    {
        _scriptId = scriptId;
        _userName = userName;
        _configPath = configPath;
    }

    public bool IsPrepared { get; private set; }

    public string ScriptId => _scriptId;

    public string ScriptDir => UserConfigManager.ScriptDir(_scriptId, _userName);

    public void PrepareScriptArea()
    {
        UserConfigManager.PrepareScriptDir(_scriptId, _userName);
    }

    public bool Begin(out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(_userName) || string.IsNullOrWhiteSpace(_configPath))
        {
            return true;
        }
        IsPrepared = UserConfigManager.PrepareForRun(_scriptId, _userName, _configPath, out error);
        return IsPrepared;
    }

    public string? PrepareRetry()
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

    public bool RestoreReplacements()
    {
        return UserConfigManager.RestoreConfigReplacements(_scriptId, _userName);
    }

    public void CleanupScriptArea()
    {
        UserConfigManager.CleanupScriptArea(_scriptId, _userName);
    }

    public string? Restore()
    {
        return IsPrepared && !string.IsNullOrWhiteSpace(_userName)
            ? UserConfigManager.RestoreAfterRun(_scriptId, _userName, _configPath)
            : null;
    }
}
