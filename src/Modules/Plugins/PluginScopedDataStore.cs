using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Serialization;

namespace NexusPipeline.Modules.Plugins;

internal sealed class PluginScopedDataStore : IPluginScopedDataStore
{
    private const int MaxScopeLength = 512;
    private const int MaxScopeSegments = 8;
    private readonly string _pluginName;
    private readonly string _root;

    public PluginScopedDataStore(string pluginName)
    {
        _pluginName = ValidateSegment(pluginName.Trim(), "插件名", 64);
        _root = Path.GetFullPath(Path.Combine(AppPaths.ConfigDir, "plugins", _pluginName, "scopes"));
    }

    public ValueTask<T?> ReadAsync<T>(string scope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = ScopePath(scope);
        if (!File.Exists(path))
        {
            return ValueTask.FromResult<T?>(default);
        }
        JsonStore.TryRead(path, out T? value, $"插件作用域数据（{_pluginName}/{scope}）");
        return ValueTask.FromResult(value);
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
        string path = Path.GetFullPath(Path.Combine(new[] { _root }.Concat(segments[..^1]).Append(segments[^1] + ".json").ToArray()));
        string prefix = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("插件作用域路径越界", nameof(scope));
        }
        return path;
    }

    internal static string[] NormalizeScope(string scope)
    {
        return NormalizeScopeSegments(scope);
    }

    /// <summary>校验并返回 scope 的规范文本形式（以 / 连接），供资产存储等其它插件端口复用。</summary>
    internal static string NormalizeScopeText(string scope)
    {
        return string.Join('/', NormalizeScopeSegments(scope));
    }

    private static string[] NormalizeScopeSegments(string scope)
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

    internal static string ValidateSegment(string value, string label, int maxLength)
    {
        if (!IsSafeSegment(value, maxLength))
        {
            throw new ArgumentException($"{label}格式不安全", nameof(value));
        }
        return value;
    }

    internal static bool IsSafeSegment(string? value, int maxLength)
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
