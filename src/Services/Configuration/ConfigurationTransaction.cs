using NexusPipeline.Services;

namespace NexusPipeline.Services.Configuration;

/// <summary>
/// 配置运行事务边界：封装 prepare、retry、sync、replace 和 rollback 原语。
/// 收尾顺序由 ConfigRunSession 负责，避免把事务状态与运行编排重新耦合。
/// </summary>
internal sealed class ConfigurationTransaction
{
    private readonly string _scriptId;
    private readonly string? _userKey;
    private readonly string _configPath;

    public ConfigurationTransaction(string scriptId, string? userKey, string? userName, string configPath)
        : this(scriptId, userKey, configPath)
    {
    }

    public ConfigurationTransaction(string scriptId, string? userKey, string configPath)
    {
        _scriptId = scriptId;
        _userKey = userKey;
        _configPath = configPath;
    }

    public bool IsPrepared { get; private set; }

    public string ScriptId => _scriptId;

    public string ScriptDir => UserConfigManager.ScriptDir(_scriptId, _userKey);

    public void PrepareScriptArea()
    {
        UserConfigManager.PrepareScriptDir(_scriptId, _userKey);
    }

    public bool Begin(out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(_userKey) || string.IsNullOrWhiteSpace(_configPath))
        {
            return true;
        }
        IsPrepared = UserConfigManager.PrepareForRun(_scriptId, _userKey, _configPath, out error);
        return IsPrepared;
    }

    public string? PrepareRetry()
    {
        if (!IsPrepared || string.IsNullOrWhiteSpace(_userKey))
        {
            return null;
        }
        return UserConfigManager.PrepareForRetry(_scriptId, _userKey, _configPath);
    }

    public void SyncToStore(bool firstCheck)
    {
        if (IsPrepared && !string.IsNullOrWhiteSpace(_userKey))
        {
            UserConfigManager.SyncConfigToStore(_scriptId, _userKey, _configPath, firstCheck);
        }
    }

    public void ApplyReplacements(List<string> replacements)
    {
        UserConfigManager.ApplyConfigReplacements(_scriptId, _userKey, _configPath, replacements);
    }

    public bool RestoreReplacements()
    {
        return UserConfigManager.RestoreConfigReplacements(_scriptId, _userKey);
    }

    public void CleanupScriptArea()
    {
        UserConfigManager.CleanupScriptArea(_scriptId, _userKey);
    }

    public string? Restore()
    {
        if (!IsPrepared || string.IsNullOrWhiteSpace(_userKey))
        {
            return null;
        }
        return UserConfigManager.RestoreAfterRun(_scriptId, _userKey, _configPath);
    }
}
