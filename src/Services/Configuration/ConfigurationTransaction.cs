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
    private readonly string? _userName;
    private readonly string _configPath;

    public ConfigurationTransaction(string scriptId, string? userKey, string? userName, string configPath)
    {
        _scriptId = scriptId;
        _userKey = userKey;
        _userName = userName;
        _configPath = configPath;
    }

    public ConfigurationTransaction(string scriptId, string? userName, string configPath)
        : this(scriptId, userName, userName, configPath)
    {
    }

    public bool IsPrepared { get; private set; }

    public string ScriptId => _scriptId;

    public string ScriptDir => UserConfigManager.ScriptDir(_scriptId, _userKey);

    public void PrepareScriptArea()
    {
        UserConfigManager.AdoptCompatibilityStore(_scriptId, _userKey, _userName);
        UserConfigManager.PrepareScriptDir(_scriptId, _userKey);
        SyncCompatibilityAlias();
    }

    public bool Begin(out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(_userKey) || string.IsNullOrWhiteSpace(_configPath))
        {
            return true;
        }
        IsPrepared = UserConfigManager.PrepareForRun(_scriptId, _userKey, _configPath, out error);
        SyncCompatibilityAlias();
        return IsPrepared;
    }

    public string? PrepareRetry()
    {
        if (!IsPrepared || string.IsNullOrWhiteSpace(_userKey))
        {
            return null;
        }
        string? error = UserConfigManager.PrepareForRetry(_scriptId, _userKey, _configPath);
        SyncCompatibilityAlias();
        return error;
    }

    public void SyncToStore(bool firstCheck)
    {
        if (IsPrepared && !string.IsNullOrWhiteSpace(_userKey))
        {
            UserConfigManager.SyncConfigToStore(_scriptId, _userKey, _configPath, firstCheck);
            SyncCompatibilityAlias();
        }
    }

    public void ApplyReplacements(List<string> replacements)
    {
        UserConfigManager.ApplyConfigReplacements(_scriptId, _userKey, _configPath, replacements);
        SyncCompatibilityAlias();
    }

    public bool RestoreReplacements()
    {
        bool restored = UserConfigManager.RestoreConfigReplacements(_scriptId, _userKey);
        SyncCompatibilityAlias();
        return restored;
    }

    public void CleanupScriptArea()
    {
        UserConfigManager.CleanupScriptArea(_scriptId, _userKey);
        UserConfigManager.CleanupCompatibilityTransient(_scriptId, _userName);
    }

    public string? Restore()
    {
        if (!IsPrepared || string.IsNullOrWhiteSpace(_userKey))
        {
            return null;
        }
        string? error = null;
        try
        {
            error = UserConfigManager.RestoreAfterRun(_scriptId, _userKey, _configPath);
        }
        finally
        {
            SyncCompatibilityAlias();
            UserConfigManager.CleanupCompatibilityTransient(_scriptId, _userName);
            UserConfigManager.CleanupCompatibilityReplacement(_scriptId, _userName);
        }
        return error;
    }

    public void SyncCompatibilityAlias()
    {
        UserConfigManager.SyncCompatibilityAlias(_scriptId, _userKey, _userName);
    }
}
