using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

internal sealed record PluginUiContributionRegistration(
    Guid Token,
    string PluginName,
    string PluginDisplayName,
    PluginUiContribution Contribution);

internal sealed class PluginUiContributionRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, PluginUiContributionRegistration> _registrations = new();

    public IDisposable Register(
        string pluginName,
        string pluginDisplayName,
        PluginUiContribution contribution)
    {
        PluginUiValidation.ValidateContribution(contribution);
        Guid token = Guid.NewGuid();
        lock (_sync)
        {
            if (_registrations.Values.Any(item =>
                string.Equals(item.PluginName, pluginName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Contribution.Id, contribution.Id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"插件 UI 贡献 ID 重复：{pluginName}/{contribution.Id}");
            }
            _registrations[token] = new PluginUiContributionRegistration(
                token,
                pluginName,
                pluginDisplayName,
                contribution);
        }
        return new CallbackDisposable(() => Remove(token));
    }

    public IReadOnlyList<PluginUiContributionRegistration> Snapshot(string? slot = null)
    {
        lock (_sync)
        {
            IEnumerable<PluginUiContributionRegistration> result = _registrations.Values;
            if (!string.IsNullOrWhiteSpace(slot))
            {
                result = result.Where(item => string.Equals(
                    item.Contribution.Slot,
                    slot,
                    StringComparison.OrdinalIgnoreCase));
            }
            return result
                .OrderBy(item => item.Contribution.Order)
                .ThenBy(item => item.PluginDisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Contribution.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public bool TryGet(
        string pluginName,
        string contributionId,
        out PluginUiContributionRegistration? registration)
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

internal sealed class PluginScopedDataStore : IPluginScopedDataStore
{
    private const int MaxScopeLength = 512;
    private const int MaxScopeSegments = 8;
    private readonly string _pluginName;

    public PluginScopedDataStore(string pluginName)
    {
        _pluginName = ValidateSegment(pluginName, "插件名", 64);
    }

    public ValueTask<T?> ReadAsync<T>(string scope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = ScopePath(scope);
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
            Logger.Warn($"插件作用域数据解析失败（{_pluginName}/{scope}），按无数据处理：{ex.Message}");
            return ValueTask.FromResult<T?>(default);
        }
    }

    public ValueTask WriteAsync<T>(string scope, T value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = ScopePath(scope);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonUtil.WriteAtomic(path, JsonSerializer.Serialize(value, JsonOpts.Indented));
        return ValueTask.CompletedTask;
    }

    public ValueTask<JsonObject?> ReadJsonAsync(string scope, CancellationToken cancellationToken = default)
    {
        return ReadAsync<JsonObject>(scope, cancellationToken);
    }

    public ValueTask WriteJsonAsync(string scope, JsonObject value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return WriteAsync(scope, value, cancellationToken);
    }

    public ValueTask DeleteAsync(string scope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = ScopePath(scope);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"删除插件作用域数据失败（{_pluginName}/{scope}）：{ex.Message}");
        }
        return ValueTask.CompletedTask;
    }

    internal static void DeleteUserData(string userId)
    {
        if (!IsSafeSegment(userId, 128))
        {
            return;
        }
        string pluginsRoot = Path.Combine(AppPaths.ConfigDir, "plugins");
        if (!Directory.Exists(pluginsRoot))
        {
            return;
        }
        foreach (string pluginDirectory in Directory.GetDirectories(pluginsRoot))
        {
            string scopes = Path.Combine(pluginDirectory, "scopes");
            TryDeleteScope(Path.Combine(scopes, "user", userId + ".json"));
            TryDeleteScope(Path.Combine(scopes, "user-script", userId));
        }
    }

    internal static void DeleteScriptData(string scriptId)
    {
        if (!IsSafeSegment(scriptId, 128))
        {
            return;
        }
        DeleteScopeFromAllPlugins("script", scriptId);
        DeleteUserScriptDataForScript(scriptId);
    }

    internal static void DeleteQueueData(string queueId)
    {
        if (!IsSafeSegment(queueId, 128))
        {
            return;
        }
        DeleteScopeFromAllPlugins("queue", queueId);
    }

    internal static void DeleteUserScriptData(string userId, string scriptId)
    {
        if (!IsSafeSegment(userId, 128) || !IsSafeSegment(scriptId, 128))
        {
            return;
        }
        string pluginsRoot = Path.Combine(AppPaths.ConfigDir, "plugins");
        if (!Directory.Exists(pluginsRoot))
        {
            return;
        }
        foreach (string pluginDirectory in Directory.GetDirectories(pluginsRoot))
        {
            TryDeleteScope(Path.Combine(
                pluginDirectory,
                "scopes",
                "user-script",
                userId,
                scriptId + ".json"));
        }
    }

    private static void DeleteScopeFromAllPlugins(params string[] segments)
    {
        string pluginsRoot = Path.Combine(AppPaths.ConfigDir, "plugins");
        if (!Directory.Exists(pluginsRoot))
        {
            return;
        }
        foreach (string pluginDirectory in Directory.GetDirectories(pluginsRoot))
        {
            TryDeleteScope(Path.Combine(new[] { pluginDirectory, "scopes" }.Concat(segments).ToArray()));
        }
    }

    private static void DeleteUserScriptDataForScript(string scriptId)
    {
        string pluginsRoot = Path.Combine(AppPaths.ConfigDir, "plugins");
        if (!Directory.Exists(pluginsRoot))
        {
            return;
        }

        foreach (string pluginDirectory in Directory.GetDirectories(pluginsRoot))
        {
            string userScriptRoot = Path.Combine(pluginDirectory, "scopes", "user-script");
            if (!Directory.Exists(userScriptRoot))
            {
                continue;
            }

            foreach (string file in Directory.GetFiles(userScriptRoot, "*.json", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(userScriptRoot, file);
                string[] parts = relative.Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2
                    && string.Equals(Path.GetFileNameWithoutExtension(parts[1]), scriptId, StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteScope(file);
                }
            }
        }
    }

    private string ScopePath(string scope)
    {
        string[] segments = NormalizeScope(scope);
        string root = Path.GetFullPath(Path.Combine(AppPaths.ConfigDir, "plugins", _pluginName, "scopes"));
        string path = Path.GetFullPath(Path.Combine(new[] { root }.Concat(segments[..^1]).Append(segments[^1] + ".json").ToArray()));
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("插件作用域路径越界", nameof(scope));
        }
        return path;
    }

    private static string[] NormalizeScope(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope.Length > MaxScopeLength
            || Path.IsPathRooted(scope) || scope.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("插件作用域格式不安全", nameof(scope));
        }
        string[] segments = scope.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is < 1 or > MaxScopeSegments || segments.Any(item => !IsSafeSegment(item, 128)))
        {
            throw new ArgumentException("插件作用域格式不安全", nameof(scope));
        }
        return segments;
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
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && value is not "." and not ".."
            && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.');
    }

    private static void TryDeleteScope(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            if (File.Exists(full))
            {
                File.Delete(full);
            }
            else if (Directory.Exists(full))
            {
                Directory.Delete(full, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"删除插件作用域数据失败（{path}）：{ex.Message}");
        }
    }
}

internal sealed record PluginWebApiRegistration(
    Guid Token,
    string PluginName,
    PluginWebApiRoute Route);

internal sealed class PluginWebApiRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, PluginWebApiRegistration> _registrations = new();

    public IDisposable Register(string pluginName, PluginWebApiRoute route)
    {
        PluginWebApiValidation.Validate(route);
        Guid token = Guid.NewGuid();
        lock (_sync)
        {
            if (_registrations.Values.Any(item =>
                string.Equals(item.PluginName, pluginName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Route.Method, route.Method, StringComparison.OrdinalIgnoreCase)
                && string.Equals(NormalizeRoute(item.Route.Route), NormalizeRoute(route.Route), StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"插件 Web API 路由重复：{pluginName}/{route.Method} {route.Route}");
            }
            _registrations[token] = new PluginWebApiRegistration(token, pluginName, route with { Method = route.Method.Trim().ToUpperInvariant(), Route = NormalizeRoute(route.Route) });
        }
        return new CallbackDisposable(() => Remove(token));
    }

    public IReadOnlyList<PluginWebApiRegistration> Snapshot()
    {
        lock (_sync)
        {
            return _registrations.Values.OrderBy(item => item.PluginName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Route.Method, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Route.Route, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public bool TryGet(string pluginName, string method, string route, out PluginWebApiRegistration? registration)
    {
        string normalized = NormalizeRoute(route);
        lock (_sync)
        {
            registration = _registrations.Values.FirstOrDefault(item =>
                string.Equals(item.PluginName, pluginName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Route.Method, method, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Route.Route, normalized, StringComparison.OrdinalIgnoreCase));
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

    internal static string NormalizeRoute(string route)
    {
        string normalized = (route ?? "").Trim().Trim('/');
        return normalized.Replace("//", "/", StringComparison.Ordinal);
    }

    private void Remove(Guid token)
    {
        lock (_sync)
        {
            _registrations.Remove(token);
        }
    }
}

internal sealed record PluginHistoryContributionRegistration(
    Guid Token,
    string PluginName,
    string PluginDisplayName,
    PluginHistoryContribution Contribution);

internal sealed class PluginHistoryContributionRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, PluginHistoryContributionRegistration> _registrations = new();

    public IDisposable Register(string pluginName, string pluginDisplayName, PluginHistoryContribution contribution)
    {
        PluginHistoryValidation.Validate(contribution);
        Guid token = Guid.NewGuid();
        lock (_sync)
        {
            if (_registrations.Values.Any(item =>
                string.Equals(item.PluginName, pluginName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Contribution.Id, contribution.Id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"插件历史贡献 ID 重复：{pluginName}/{contribution.Id}");
            }
            _registrations[token] = new PluginHistoryContributionRegistration(token, pluginName, pluginDisplayName, contribution);
        }
        return new CallbackDisposable(() => Remove(token));
    }

    public IReadOnlyList<PluginHistoryContributionRegistration> Snapshot()
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

internal static class PluginUiValidation
{
    private static readonly HashSet<string> AllowedKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        PluginUiContributionKinds.Form,
        PluginUiContributionKinds.Badge,
        PluginUiContributionKinds.Card,
    };

    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "textarea", "secret", "switch", "select", "multi-select", "status",
        "number", "color", "range", "url",
    };

    private static readonly HashSet<string> AllowedTones = new(StringComparer.OrdinalIgnoreCase)
    {
        "muted", "blue", "ok", "warn", "bad",
    };

    public static void ValidateContribution(PluginUiContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        if (!IsSafeKey(contribution.Id, 64)) throw new InvalidDataException("插件 UI 贡献 ID 无效");
        if (!PluginUiSlots.All.Contains(contribution.Slot)) throw new InvalidDataException($"插件 UI slot 不受支持：{contribution.Slot}");
        if (!AllowedKinds.Contains(contribution.Kind)) throw new InvalidDataException($"插件 UI 贡献类型不受支持：{contribution.Kind}");
        if (contribution.Title is null || contribution.Title.Length > 128
            || (contribution.Kind is not PluginUiContributionKinds.Badge && string.IsNullOrWhiteSpace(contribution.Title)))
        {
            throw new InvalidDataException("插件 UI 贡献标题无效");
        }
        if (contribution.Description is null || contribution.Description.Length > 2048) throw new InvalidDataException("插件 UI 贡献说明无效");
        if (contribution.Fields is not null && contribution.Fields.Count > 64) throw new InvalidDataException("插件 UI 贡献字段过多");
        if (contribution.ReadHandler is null && contribution.Kind is PluginUiContributionKinds.Badge or PluginUiContributionKinds.Card)
        {
            throw new InvalidDataException("插件 UI 展示贡献缺少读取处理器");
        }
        if (contribution.Kind == PluginUiContributionKinds.Form && contribution.SaveHandler is null && contribution.ReadHandler is null)
        {
            throw new InvalidDataException("插件 UI 表单缺少处理器");
        }
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PluginUiField field in contribution.Fields ?? Array.Empty<PluginUiField>())
        {
            if (!IsSafeKey(field.Key, 64) || !keys.Add(field.Key)) throw new InvalidDataException($"插件 UI 字段 key 无效或重复：{field.Key}");
            if (field.Label is null || field.Description is null || field.Placeholder is null || string.IsNullOrWhiteSpace(field.Type) || !AllowedTypes.Contains(field.Type)) throw new InvalidDataException($"插件 UI 字段定义无效：{field.Key}");
            if (field.Label.Length is 0 or > 128 || field.Description.Length > 1024 || field.Placeholder.Length > 512 || field.MaxLength < 0 || field.MaxLength > 1024 * 1024) throw new InvalidDataException($"插件 UI 字段文本或长度无效：{field.Key}");
            if (field.Min is double.NaN or double.PositiveInfinity or double.NegativeInfinity
                || field.Max is double.NaN or double.PositiveInfinity or double.NegativeInfinity
                || field.Step is double.NaN or double.PositiveInfinity or double.NegativeInfinity
                || field.Min.HasValue && field.Max.HasValue && field.Min.Value > field.Max.Value
                || field.Step.HasValue && field.Step.Value <= 0)
            {
                throw new InvalidDataException($"插件 UI 数值范围无效：{field.Key}");
            }
            if (field.Type.Equals("select", StringComparison.OrdinalIgnoreCase) || field.Type.Equals("multi-select", StringComparison.OrdinalIgnoreCase))
            {
                if (field.Options is null || field.Options.Count is < 1 or > 256 || field.Options.Any(option => option is null || string.IsNullOrWhiteSpace(option.Value) || option.Value.Length > 128 || string.IsNullOrWhiteSpace(option.Label) || option.Label.Length > 128)) throw new InvalidDataException($"插件 UI 选择字段 options 无效：{field.Key}");
            }
        }
    }

    public static bool TrySanitizePayload(JsonObject? payload, out JsonObject sanitized, out string error)
    {
        sanitized = new JsonObject();
        error = "";
        if (payload is null) return true;
        if (payload.Count > 64) { error = "插件 UI 返回字段过多"; return false; }
        foreach ((string key, JsonNode? value) in payload)
        {
            if (!IsSafeKey(key, 64)) { error = "插件 UI 返回字段名无效"; return false; }
            string text = value?.ToJsonString() ?? "null";
            if (text.Length > 1024 * 1024) { error = "插件 UI 返回内容过大"; return false; }
            sanitized[key] = value?.DeepClone();
        }
        return true;
    }

    public static bool TrySanitizeRead(
        PluginUiContribution contribution,
        JsonObject? payload,
        out JsonObject sanitized,
        out string error)
    {
        if (!TrySanitizePayload(payload, out sanitized, out error))
        {
            return false;
        }
        foreach (PluginUiField field in contribution.Fields ?? Array.Empty<PluginUiField>())
        {
            if (!field.Type.Equals("secret", StringComparison.OrdinalIgnoreCase)
                || !sanitized.TryGetPropertyValue(field.Key, out JsonNode? value))
            {
                continue;
            }
            bool configured = value is not null;
            if (value is JsonObject secretObject
                && secretObject.TryGetPropertyValue("configured", out JsonNode? configuredNode))
            {
                try
                {
                    configured = configuredNode?.GetValue<bool>() == true;
                }
                catch (InvalidOperationException)
                {
                    configured = true;
                }
            }
            sanitized[field.Key] = new JsonObject { ["configured"] = configured };
        }
        return true;
    }

    public static bool TryValidateValues(PluginUiContribution contribution, JsonObject? values, out string error)
    {
        error = "";
        values ??= new JsonObject();
        if (values.Count > 64)
        {
            error = "插件 UI 表单字段过多";
            return false;
        }
        var fields = contribution.Fields ?? Array.Empty<PluginUiField>();
        var knownKeys = fields.Select(field => field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, JsonNode? value) in values)
        {
            if (!knownKeys.Contains(key))
            {
                error = $"插件 UI 表单字段未定义：{key}";
                return false;
            }
        }

        foreach (PluginUiField field in fields)
        {
            if (!values.TryGetPropertyValue(field.Key, out JsonNode? value))
            {
                if (field.Required)
                {
                    error = $"插件 UI 必填字段缺失：{field.Key}";
                    return false;
                }
                continue;
            }
            if (field.ReadOnly || field.Type.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                error = $"插件 UI 字段不可写：{field.Key}";
                return false;
            }

            if (!TryValidateValue(field, value, out error)) return false;
        }

        return true;
    }

    private static bool TryValidateValue(PluginUiField field, JsonNode? value, out string error)
    {
        error = "";
        if (value is null)
        {
            if (field.Required) error = $"插件 UI 字段不能为空：{field.Key}";
            return error.Length == 0;
        }

        try
        {
            string type = field.Type.Trim().ToLowerInvariant();
            switch (type)
            {
                case "text":
                case "textarea":
                case "status":
                    if (!TryGetString(value, out string? text) || text is null || (field.MaxLength > 0 && text.Length > field.MaxLength))
                    {
                        error = $"插件 UI 字段文本无效：{field.Key}";
                        return false;
                    }
                    break;
                case "secret":
                    if (value is not JsonObject secret || !secret.TryGetPropertyValue("action", out JsonNode? actionNode) || !TryGetString(actionNode, out string? action) || action is null)
                    {
                        error = $"插件 UI 密钥字段格式无效：{field.Key}";
                        return false;
                    }
                    action = action.Trim().ToLowerInvariant();
                    if (action is "keep" or "clear")
                    {
                        break;
                    }
                    if (action != "set"
                        || !secret.TryGetPropertyValue("value", out JsonNode? secretValue)
                        || !TryGetString(secretValue, out string? secretText)
                        || secretText is null
                        || (field.MaxLength > 0 && secretText.Length > field.MaxLength))
                    {
                        error = $"插件 UI 密钥字段格式无效：{field.Key}";
                        return false;
                    }
                    break;
                case "url":
                    if (!TryGetString(value, out string? url) || url is null || (field.MaxLength > 0 && url.Length > field.MaxLength) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
                    {
                        error = $"插件 UI URL 字段无效：{field.Key}";
                        return false;
                    }
                    break;
                case "color":
                    if (!TryGetString(value, out string? color) || color is null || (field.MaxLength > 0 && color.Length > field.MaxLength) || !IsValidColor(color))
                    {
                        error = $"插件 UI 颜色字段无效：{field.Key}";
                        return false;
                    }
                    break;
                case "switch":
                    if (!TryGetBool(value, out _))
                    {
                        error = $"插件 UI 开关字段无效：{field.Key}";
                        return false;
                    }
                    break;
                case "number":
                case "range":
                    if (!TryGetDouble(value, out double number) || double.IsNaN(number) || double.IsInfinity(number) || (field.Min.HasValue && number < field.Min.Value) || (field.Max.HasValue && number > field.Max.Value)
                        || field.Step.HasValue && field.Min.HasValue && Math.Abs(((number - field.Min.Value) / field.Step.Value) - Math.Round((number - field.Min.Value) / field.Step.Value)) > 1e-9)
                    {
                        error = $"插件 UI 数值字段无效：{field.Key}";
                        return false;
                    }
                    break;
                case "select":
                    if (!TryGetString(value, out string? selected) || field.Options is null || !field.Options.Any(option => string.Equals(option.Value, selected, StringComparison.Ordinal)))
                    {
                        error = $"插件 UI 选择字段无效：{field.Key}";
                        return false;
                    }
                    break;
                case "multi-select":
                    if (value is not JsonArray selectedValues || selectedValues.Count > 256 || field.Options is null || selectedValues.Any(item => !TryGetString(item, out string? optionValue) || !field.Options.Any(option => string.Equals(option.Value, optionValue, StringComparison.Ordinal))))
                    {
                        error = $"插件 UI 多选字段无效：{field.Key}";
                        return false;
                    }
                    break;
                default:
                    error = $"插件 UI 字段类型不受支持：{field.Key}";
                    return false;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException)
        {
            error = $"插件 UI 字段值格式无效：{field.Key}";
            return false;
        }

        return true;
    }

    private static bool TryGetString(JsonNode? value, out string? text)
    {
        text = null;
        if (value is not JsonValue) return false;
        try
        {
            text = value.GetValue<string>();
            return text is not null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryGetBool(JsonNode? value, out bool result)
    {
        result = false;
        if (value is not JsonValue) return false;
        try
        {
            result = value.GetValue<bool>();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryGetDouble(JsonNode? value, out double result)
    {
        result = 0;
        if (value is not JsonValue) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(value.ToJsonString());
            return document.RootElement.ValueKind == JsonValueKind.Number
                && document.RootElement.TryGetDouble(out result);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsValidColor(string value)
    {
        if (value.Length is not (4 or 7) || value[0] != '#') return false;
        return value[1..].All(ch => char.IsAsciiLetterOrDigit(ch) && (ch is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'));
    }

    public static bool TrySanitizeHistoryDisplay(PluginHistoryDisplay? display, out PluginHistoryDisplay? sanitized, out string error)
    {
        sanitized = null;
        error = "";
        if (display is null) return true;
        if (!IsSafeKey(display.Id, 64) || string.IsNullOrWhiteSpace(display.Title) || display.Title.Length > 128) { error = "插件历史展示 ID 或标题无效"; return false; }
        var badges = new List<PluginUiBadge>();
        foreach (PluginUiBadge? badge in display.Badges ?? Array.Empty<PluginUiBadge>())
        {
            if (badge is null || string.IsNullOrWhiteSpace(badge.Label) || badge.Label.Length > 64 || !AllowedTones.Contains(badge.Tone) || badge.Title is null || badge.Title.Length > 256) { error = "插件历史徽章无效"; return false; }
            badges.Add(new PluginUiBadge(badge.Label.Trim(), badge.Tone.Trim().ToLowerInvariant(), badge.Title));
        }
        var fields = new List<PluginUiFieldValue>();
        foreach (PluginUiFieldValue? field in display.Fields ?? Array.Empty<PluginUiFieldValue>())
        {
            if (field is null || string.IsNullOrWhiteSpace(field.Label) || field.Label.Length > 128 || field.Value is null || field.Value.Length > 2048 || !AllowedTones.Contains(field.Tone)) { error = "插件历史字段无效"; return false; }
            fields.Add(new PluginUiFieldValue(field.Label, field.Value, field.Tone.Trim().ToLowerInvariant()));
        }
        if (badges.Count > 32 || fields.Count > 64) { error = "插件历史展示数量超限"; return false; }
        sanitized = new PluginHistoryDisplay(display.Id, display.Title.Trim(), badges, fields);
        return true;
    }

    private static bool IsSafeKey(string? value, int maxLength)
    {
        return !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.');
    }
}

internal static class PluginWebApiValidation
{
    private static readonly HashSet<string> Methods = new(StringComparer.OrdinalIgnoreCase) { "GET", "POST", "PUT", "PATCH", "DELETE" };

    public static void Validate(PluginWebApiRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (!Methods.Contains(route.Method?.Trim() ?? "")) throw new InvalidDataException("插件 Web API method 无效");
        string normalized = PluginWebApiRegistry.NormalizeRoute(route.Route);
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 256 || normalized.StartsWith("/", StringComparison.Ordinal) || normalized.Contains("\\", StringComparison.Ordinal) || normalized.Split('/').Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".." || !segment.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' ))) throw new InvalidDataException("插件 Web API route 格式不安全");
        ArgumentNullException.ThrowIfNull(route.Handler);
    }
}

internal static class PluginHistoryValidation
{
    public static void Validate(PluginHistoryContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        if (string.IsNullOrWhiteSpace(contribution.Id) || contribution.Id.Length > 64 || !contribution.Id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')) throw new InvalidDataException("插件历史贡献 ID 无效");
        ArgumentNullException.ThrowIfNull(contribution.Handler);
    }
}
