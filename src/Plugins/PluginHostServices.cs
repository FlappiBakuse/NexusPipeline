using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Persistence;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Services.Networking;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

internal sealed record PluginUserGlobalManagementRegistration(
    Guid Token,
    string PluginName,
    string PluginDisplayName,
    PluginUserGlobalManagementContribution Contribution);

internal sealed class PluginUserGlobalManagementRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, PluginUserGlobalManagementRegistration> _registrations = new();

    public IDisposable Register(
        string pluginName,
        string pluginDisplayName,
        PluginUserGlobalManagementContribution contribution)
    {
        PluginContributionValidation.ValidateContribution(contribution);
        Guid token = Guid.NewGuid();
        lock (_sync)
        {
            if (_registrations.Values.Any(item =>
                string.Equals(item.PluginName, pluginName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Contribution.Id, contribution.Id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"插件贡献 ID 重复：{pluginName}/{contribution.Id}");
            }
            _registrations[token] = new PluginUserGlobalManagementRegistration(
                token,
                pluginName,
                pluginDisplayName,
                contribution);
        }
        return new CallbackDisposable(() => Remove(token));
    }

    public IReadOnlyList<PluginUserGlobalManagementRegistration> Snapshot()
    {
        lock (_sync)
        {
            return _registrations.Values
                .OrderBy(item => item.Contribution.Order)
                .ThenBy(item => item.PluginDisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Contribution.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public bool TryGet(
        string pluginName,
        string contributionId,
        out PluginUserGlobalManagementRegistration? registration)
    {
        lock (_sync)
        {
            registration = _registrations.Values.FirstOrDefault(item =>
                string.Equals(item.PluginName, pluginName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Contribution.Id, contributionId, StringComparison.OrdinalIgnoreCase));
            return registration is not null;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _registrations.Clear();
        }
    }

    private void Remove(Guid token)
    {
        lock (_sync)
        {
            _registrations.Remove(token);
        }
    }
}

internal sealed record PluginUserListBadgeRegistration(
    Guid Token,
    string PluginName,
    string PluginDisplayName,
    PluginUserListBadgeContribution Contribution);

internal sealed class PluginUserListBadgeRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, PluginUserListBadgeRegistration> _registrations = new();

    public IDisposable Register(
        string pluginName,
        string pluginDisplayName,
        PluginUserListBadgeContribution contribution)
    {
        PluginContributionValidation.ValidateUserListBadgeContribution(contribution);
        Guid token = Guid.NewGuid();
        lock (_sync)
        {
            if (_registrations.Values.Any(item =>
                string.Equals(item.PluginName, pluginName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Contribution.Id, contribution.Id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"插件用户列表徽章贡献 ID 重复：{pluginName}/{contribution.Id}");
            }
            _registrations[token] = new PluginUserListBadgeRegistration(
                token,
                pluginName,
                pluginDisplayName,
                contribution);
        }
        return new CallbackDisposable(() => Remove(token));
    }

    public IReadOnlyList<PluginUserListBadgeRegistration> Snapshot()
    {
        lock (_sync)
        {
            return _registrations.Values
                .OrderBy(item => item.Contribution.Order)
                .ThenBy(item => item.PluginDisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Contribution.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _registrations.Clear();
        }
    }

    private void Remove(Guid token)
    {
        lock (_sync)
        {
            _registrations.Remove(token);
        }
    }
}

internal sealed class PluginExecutionEventRegistry
{
    private sealed record Subscription(Guid Token, string PluginName, Func<PluginUserRunStartingEvent, ValueTask> Handler);

    private readonly object _sync = new();
    private readonly Dictionary<Guid, Subscription> _subscriptions = new();
    private readonly Action<string, Exception> _reportError;

    public PluginExecutionEventRegistry(Action<string, Exception> reportError)
    {
        _reportError = reportError;
    }

    public IDisposable Subscribe(
        string pluginName,
        Func<PluginUserRunStartingEvent, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Guid token = Guid.NewGuid();
        lock (_sync)
        {
            _subscriptions[token] = new Subscription(token, pluginName, handler);
        }
        return new CallbackDisposable(() => Remove(token));
    }

    public void Publish(PluginUserRunStartingEvent eventData)
    {
        Subscription[] subscriptions;
        lock (_sync)
        {
            subscriptions = _subscriptions.Values.ToArray();
        }
        foreach (Subscription subscription in subscriptions)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await subscription.Handler(eventData).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _reportError(subscription.PluginName, ex);
                }
            });
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _subscriptions.Clear();
        }
    }

    private void Remove(Guid token)
    {
        lock (_sync)
        {
            _subscriptions.Remove(token);
        }
    }
}

internal sealed class PluginUserDataStore : IPluginUserDataStore
{
    private const int MaxUserIdLength = 128;
    private const int MaxSecretKeyLength = 128;

    private readonly string _pluginName;

    public PluginUserDataStore(string pluginName)
    {
        _pluginName = ValidateSegment(pluginName, "插件名", 64);
    }

    public ValueTask<T?> ReadConfigAsync<T>(string userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = UserConfigPath(userId);
        if (!File.Exists(path))
        {
            return ValueTask.FromResult<T?>(default);
        }
        try
        {
            return ValueTask.FromResult(JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOpts.Default));
        }
        catch (Exception ex)
        {
            Logger.Warn($"插件用户配置解析失败（{_pluginName}/{userId}），按无配置处理：{ex.Message}");
            return ValueTask.FromResult<T?>(default);
        }
    }

    public ValueTask WriteConfigAsync<T>(string userId, T value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = UserConfigPath(userId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonUtil.WriteAtomic(path, JsonSerializer.Serialize(value, JsonOpts.Indented));
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> GetSecretAsync(
        string userId,
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        key = ValidateSegment(key, "密钥名", MaxSecretKeyLength);
        JsonObject root = ReadSecrets(UserSecretsPath(userId));
        string stored = root[key]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(stored))
        {
            return ValueTask.FromResult<string?>(null);
        }
        return ValueTask.FromResult(SecretStore.TryDecrypt(stored, out string? plain) ? plain : null);
    }

    public ValueTask SetSecretAsync(
        string userId,
        string key,
        string? value,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        key = ValidateSegment(key, "密钥名", MaxSecretKeyLength);
        string path = UserSecretsPath(userId);
        JsonObject root = ReadSecrets(path);
        if (string.IsNullOrWhiteSpace(value))
        {
            root.Remove(key);
        }
        else
        {
            root[key] = SecretStore.Encrypt(value);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonUtil.WriteAtomic(path, root.ToJsonString(JsonOpts.Indented));
        return ValueTask.CompletedTask;
    }

    public static void DeleteAllForUser(string userId)
    {
        if (!IsSafeSegment(userId, MaxUserIdLength) || !Directory.Exists(Path.Combine(AppPaths.ConfigDir, "plugins")))
        {
            return;
        }
        foreach (string pluginDirectory in Directory.GetDirectories(Path.Combine(AppPaths.ConfigDir, "plugins")))
        {
            string usersDirectory = Path.Combine(pluginDirectory, "users");
            string configPath = Path.Combine(usersDirectory, userId + ".json");
            string secretsPath = Path.Combine(usersDirectory, userId + ".secrets.json");
            TryDelete(configPath);
            TryDelete(secretsPath);
        }
    }

    private string UserConfigPath(string userId)
    {
        userId = ValidateSegment(userId, "用户 ID", MaxUserIdLength);
        return Path.Combine(UserDirectory(), userId + ".json");
    }

    private string UserSecretsPath(string userId)
    {
        userId = ValidateSegment(userId, "用户 ID", MaxUserIdLength);
        return Path.Combine(UserDirectory(), userId + ".secrets.json");
    }

    private string UserDirectory() => Path.Combine(
        AppPaths.ConfigDir,
        "plugins",
        _pluginName,
        "users");

    private static JsonObject ReadSecrets(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject();
        }
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex)
        {
            Logger.Warn($"插件用户密钥文件损坏（{path}），继续写入：{ex.Message}");
            return new JsonObject();
        }
    }

    private static string ValidateSegment(string value, string label, int maxLength)
    {
        if (!IsSafeSegment(value, maxLength))
        {
            throw new ArgumentException($"{label}格式不安全", nameof(value));
        }
        return value;
    }

    private static bool IsSafeSegment(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value is "." or "..")
        {
            return false;
        }
        return value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.');
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"删除插件用户数据失败（{path}）：{ex.Message}");
        }
    }
}

internal sealed class PluginHttpClientFactory : IPluginHttpClientFactory
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private readonly OutboundHttpClientProvider _provider;

    public PluginHttpClientFactory(OutboundHttpClientProvider provider)
    {
        _provider = provider;
    }

    public HttpClient CreateClient(
        Uri? destination = null,
        TimeSpan? timeout = null,
        bool allowAutoRedirect = false)
    {
        TimeSpan effectiveTimeout = timeout ?? DefaultTimeout;
        if (effectiveTimeout <= TimeSpan.Zero || effectiveTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "插件 HTTP 超时必须大于 0 且不超过 10 分钟");
        }
        return _provider.CreateClient(destination, effectiveTimeout, allowAutoRedirect);
    }
}

internal static class PluginContributionValidation
{
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text",
        "textarea",
        "secret",
        "switch",
        "select",
        "multi-select",
        "status",
    };

    private static readonly HashSet<string> AllowedBadgeTones = new(StringComparer.OrdinalIgnoreCase)
    {
        "muted",
        "blue",
        "ok",
        "warn",
        "bad",
    };

    public static void ValidateUserListBadgeContribution(PluginUserListBadgeContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        if (!IsSafeKey(contribution.Id, 64))
        {
            throw new InvalidDataException("插件用户列表徽章贡献 ID 无效");
        }
        if (contribution.ReadHandler is null)
        {
            throw new InvalidDataException("插件用户列表徽章贡献缺少读取处理器");
        }
    }

    public static bool TrySanitizeUserListBadge(
        PluginUserListBadge? badge,
        out PluginUserListBadge? sanitized,
        out string error)
    {
        sanitized = null;
        error = "";
        if (badge is null)
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(badge.Label) || badge.Label.Length > 64)
        {
            error = "插件用户列表徽章 label 无效";
            return false;
        }
        if (badge.Tone is null)
        {
            error = "插件用户列表徽章 tone 无效";
            return false;
        }
        string tone = badge.Tone.Trim().ToLowerInvariant();
        if (!AllowedBadgeTones.Contains(tone))
        {
            error = $"插件用户列表徽章 tone 不受支持：{badge.Tone}";
            return false;
        }
        if (badge.Title is null || badge.Title.Length > 256)
        {
            error = "插件用户列表徽章 title 无效";
            return false;
        }
        sanitized = new PluginUserListBadge(badge.Label.Trim(), tone, badge.Title);
        return true;
    }

    public static void ValidateContribution(PluginUserGlobalManagementContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        ArgumentNullException.ThrowIfNull(contribution.Fields);
        if (!IsSafeKey(contribution.Id, 64)) throw new InvalidDataException("插件贡献 ID 无效");
        if (string.IsNullOrWhiteSpace(contribution.Title) || contribution.Title.Length > 128)
        {
            throw new InvalidDataException("插件贡献标题无效");
        }
        if (contribution.Description is null || contribution.Description.Length > 2048) throw new InvalidDataException("插件贡献说明无效");
        if (contribution.Fields.Count > 64) throw new InvalidDataException("插件贡献字段过多");
        if (contribution.ReadHandler is null || contribution.SaveHandler is null)
        {
            throw new InvalidDataException("插件贡献缺少处理器");
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PluginUserGlobalManagementField field in contribution.Fields)
        {
            ArgumentNullException.ThrowIfNull(field);
            if (!IsSafeKey(field.Key, 64) || !keys.Add(field.Key)) throw new InvalidDataException("插件贡献字段 key 无效或重复");
            if (field.Type is null || field.Label is null || field.Description is null || field.Placeholder is null)
            {
                throw new InvalidDataException($"插件贡献字段定义无效：{field.Key}");
            }
            string type = field.Type.Trim().ToLowerInvariant();
            if (!AllowedTypes.Contains(type)) throw new InvalidDataException($"插件贡献字段类型不受支持：{field.Type}");
            if (field.Label.Length is 0 or > 128 || field.Description.Length > 1024 || field.Placeholder.Length > 512)
            {
                throw new InvalidDataException($"插件贡献字段展示文本无效：{field.Key}");
            }
            if (field.MaxLength < 0 || field.MaxLength > 1024 * 1024)
            {
                throw new InvalidDataException($"插件贡献字段 maxLength 无效：{field.Key}");
            }
            if (type is "select" or "multi-select")
            {
                if (field.Options is null || field.Options.Count == 0 || field.Options.Count > 256)
                {
                    throw new InvalidDataException($"选择字段缺少有效 options：{field.Key}");
                }
                var optionValues = new HashSet<string>(StringComparer.Ordinal);
                foreach (PluginUserGlobalManagementOption option in field.Options)
                {
                    ArgumentNullException.ThrowIfNull(option);
                    if (string.IsNullOrWhiteSpace(option.Value) || option.Value.Length > 128
                        || option.Label is null || option.Label.Length is 0 or > 128 || !optionValues.Add(option.Value))
                    {
                        throw new InvalidDataException($"选择字段 options 无效：{field.Key}");
                    }
                }
            }
        }
    }

    public static bool TryValidateSave(
        PluginUserGlobalManagementRegistration registration,
        JsonObject values,
        out JsonObject sanitized,
        out string error)
    {
        sanitized = new JsonObject();
        error = "";
        var fields = registration.Contribution.Fields.ToDictionary(
            field => field.Key,
            StringComparer.OrdinalIgnoreCase);
        foreach ((string key, JsonNode? value) in values)
        {
            if (!fields.TryGetValue(key, out PluginUserGlobalManagementField? field))
            {
                error = $"未知插件字段：{key}";
                return false;
            }
            if (field.ReadOnly || field.Type.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                error = $"插件字段不可写：{key}";
                return false;
            }
            if (!TryValidateValue(field, value, out JsonNode? copy, out error))
            {
                return false;
            }
            // 统一使用声明中的字段名传给插件，避免大小写不同的 JSON key 让插件读取不到已校验的值。
            sanitized[field.Key] = copy;
        }
        foreach (PluginUserGlobalManagementField field in registration.Contribution.Fields)
        {
            if (!field.Required || sanitized.ContainsKey(field.Key))
            {
                continue;
            }
            error = $"缺少必填插件字段：{field.Key}";
            return false;
        }
        return true;
    }

    public static JsonObject SanitizeRead(
        PluginUserGlobalManagementRegistration registration,
        JsonObject? values)
    {
        var sanitized = new JsonObject();
        if (values is null)
        {
            return sanitized;
        }
        foreach (PluginUserGlobalManagementField field in registration.Contribution.Fields)
        {
            JsonNode? value = values[field.Key];
            if (field.Type.Equals("secret", StringComparison.OrdinalIgnoreCase))
            {
                bool configured = value switch
                {
                    JsonObject secret => IsConfigured(secret),
                    JsonValue scalar => scalar.TryGetValue<string>(out string? scalarValue)
                        && !string.IsNullOrWhiteSpace(scalarValue),
                    _ => false,
                };
                sanitized[field.Key] = new JsonObject { ["configured"] = configured };
                continue;
            }
            if (TryValidateValue(field, value, out JsonNode? copy, out _))
            {
                sanitized[field.Key] = copy;
            }
        }
        return sanitized;
    }

    private static bool TryValidateValue(
        PluginUserGlobalManagementField field,
        JsonNode? value,
        out JsonNode? copy,
        out string error)
    {
        copy = null;
        error = $"插件字段格式不正确：{field.Key}";
        string type = field.Type.Trim().ToLowerInvariant();
        switch (type)
        {
            case "switch":
                if (value is not JsonValue switchValue || !switchValue.TryGetValue<bool>(out bool enabled)) return false;
                copy = JsonValue.Create(enabled);
                return true;
            case "text":
            case "textarea":
            case "select":
            case "status":
                if (value is not JsonValue textValue || !textValue.TryGetValue<string>(out string? text)) return false;
                if (field.MaxLength > 0 && text.Length > field.MaxLength)
                {
                    error = $"插件字段超过长度限制：{field.Key}";
                    return false;
                }
                if (type == "select" && !IsOption(field, text))
                {
                    error = $"插件字段选项无效：{field.Key}";
                    return false;
                }
                copy = JsonValue.Create(text);
                return true;
            case "multi-select":
                if (value is not JsonArray array) return false;
                var selected = new JsonArray();
                foreach (JsonNode? item in array)
                {
                    if (item is not JsonValue itemValue || !itemValue.TryGetValue<string>(out string? option) || !IsOption(field, option))
                    {
                        error = $"插件字段选项无效：{field.Key}";
                        return false;
                    }
                    if (!selected.Any(existing => string.Equals(existing?.ToString(), option, StringComparison.Ordinal)))
                    {
                        selected.Add(option);
                    }
                }
                copy = selected;
                return true;
            case "secret":
                if (value is not JsonObject secret || secret["action"] is null) return false;
                string action = secret["action"]?.ToString()?.Trim().ToLowerInvariant() ?? "";
                if (action is not ("keep" or "clear" or "set"))
                {
                    error = $"插件密钥操作无效：{field.Key}";
                    return false;
                }
                var secretCopy = new JsonObject { ["action"] = action };
                if (action == "set")
                {
                    if (secret["value"] is not JsonValue secretValueNode
                        || !secretValueNode.TryGetValue<string>(out string? secretValue))
                    {
                        error = $"插件密钥值格式不正确：{field.Key}";
                        return false;
                    }
                    if (field.MaxLength > 0 && secretValue.Length > field.MaxLength)
                    {
                        error = $"插件密钥超过长度限制：{field.Key}";
                        return false;
                    }
                    secretCopy["value"] = secretValue;
                }
                copy = secretCopy;
                return true;
            default:
                return false;
        }
    }

    private static bool IsOption(PluginUserGlobalManagementField field, string value) =>
        field.Options?.Any(option => string.Equals(option.Value, value, StringComparison.Ordinal)) == true;

    private static bool IsConfigured(JsonObject secret)
    {
        if (secret["configured"] is JsonValue configuredNode
            && configuredNode.TryGetValue<bool>(out bool configured)
            && configured)
        {
            return true;
        }
        return secret["value"] is JsonValue valueNode
            && valueNode.TryGetValue<string>(out string? value)
            && !string.IsNullOrWhiteSpace(value);
    }

    private static bool IsSafeKey(string? value, int maxLength)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.');
    }
}

internal sealed class CallbackDisposable : IDisposable
{
    private Action? _dispose;

    public CallbackDisposable(Action dispose)
    {
        _dispose = dispose;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
