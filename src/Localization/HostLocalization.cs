using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.App.Contracts;
using NexusPipeline.Utilities;

namespace NexusPipeline.Localization;

/// <summary>宿主协议、通知、日志和基础 UI 文案的稳定翻译表。</summary>
internal static class HostLocalization
{
    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> ResourceCache =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

    private static readonly object ResourceSync = new();

    private static readonly IReadOnlyDictionary<string, string> English =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth.required"] = "An access token is required (Authorization: Bearer <token>)",
            ["http.not_found"] = "Not found",
            ["http.method_not_allowed"] = "Method not allowed",
            ["http.body_too_large"] = "Request body is too large (limit {0} MB)",
            ["validation.invalid_request"] = "The request is invalid",
            ["validation.invalid_json"] = "The request body is invalid",
            ["plugin.contribution_not_found"] = "The plugin contribution does not exist or the plugin is disabled",
            ["plugin.settings_invalid"] = "The plugin settings format is invalid",
            ["plugin.settings_read_timeout"] = "Reading plugin settings timed out",
            ["plugin.settings_save_timeout"] = "Saving plugin settings timed out",
            ["plugin.settings_read_failed"] = "Reading plugin settings failed",
            ["plugin.settings_save_failed"] = "Saving plugin settings failed",
            ["plugin.ui_not_found"] = "The plugin UI contribution does not exist or the plugin is disabled",
            ["plugin.ui_action_not_supported"] = "This plugin UI contribution does not support the action",
            ["plugin.ui_save_not_supported"] = "This plugin UI contribution does not support saving",
            ["plugin.error"] = "The plugin operation failed",
            ["plugin.not_found"] = "Plugin not found: {0}",
            ["script.name_required"] = "Script name is required",
            ["user.name_invalid"] = "The user name is required and contains invalid characters",
            ["history.id_required"] = "Record ID is required",
            ["history.not_found"] = "Record not found",
            ["fs.path_not_found"] = "Directory not found: {0}",
            ["fs.path_forbidden"] = "The path is outside the allowed browsing scope",
            ["fs.read_failed"] = "Failed to read directory: {0}",
            ["system.local_only"] = "This operation is available only to local requests",
        };

    internal static IEnumerable<string> EnglishFallbackKeys => English.Keys;

    public static string Translate(
        string? key,
        string fallback,
        string? locale = null,
        IReadOnlyList<object?>? args = null)
    {
        string normalized = LocaleCatalog.Normalize(locale ?? LocaleContext.Current);
        if (string.IsNullOrWhiteSpace(key))
        {
            return fallback;
        }
        if (!TryGetTemplate(normalized, key!, out string? template)
            && !English.TryGetValue(key!, out template))
        {
            return fallback;
        }
        return Format(template ?? fallback, args);
    }

    /// <summary>按稳定资源 key 渲染宿主输出；资源缺失时返回调用方提供的安全回退文本。</summary>
    public static string TranslateNamed(
        string? key,
        string fallback,
        IReadOnlyDictionary<string, object?>? args = null,
        string? locale = null)
    {
        string normalized = LocaleCatalog.Normalize(locale ?? LocaleContext.Current);
        if (string.IsNullOrWhiteSpace(key)
            || (!TryGetTemplate(normalized, key!, out string? template)
                && !English.TryGetValue(key!, out template)))
        {
            return fallback;
        }
        string value = template!;
        foreach ((string name, object? replacement) in args ?? new Dictionary<string, object?>())
        {
            value = value.Replace("{" + name + "}", Convert.ToString(replacement, CultureInfo.InvariantCulture) ?? "", StringComparison.Ordinal);
        }
        return value;
    }

    /// <summary>将 Control API 的机器错误码投影为 CLI 等宿主输出使用的本地化文字。</summary>
    public static string TranslateApiError(
        string code,
        JsonNode? args,
        int statusCode,
        string? locale = null)
    {
        var named = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (args is JsonObject objectArgs)
        {
            foreach ((string key, JsonNode? value) in objectArgs)
            {
                named[key] = JsonScalar(value);
            }
        }
        string fallback = code switch
        {
            "auth_required" => "需要访问令牌",
            "not_found" => "未找到",
            "method_not_allowed" => "请求方法不支持",
            "validation_error" or "invalid_request" => "请求格式无效",
            "operation_forbidden" or "local_only" => "当前操作不被允许",
            "resource_busy" or "pending" => "资源当前忙碌，请稍后重试",
            "timeout" => "请求超时",
            "service_unavailable" or "repository_unavailable" => "服务暂不可用",
            "internal_error" => "服务内部错误",
            _ => $"服务返回 HTTP {statusCode}（{code}）",
        };
        return TranslateNamed("api.error." + code, fallback, named, locale);
    }

    /// <summary>
    /// 将应用层错误投影为当前适配器的最终用户文案。
    /// MessageKey/MessageArgs 优先；旧调用路径只有源语言 Message 时，英文环境拒绝泄漏中文业务句子。
    /// </summary>
    public static string TranslateOperationError(OperationError error, string? locale = null)
    {
        string normalized = LocaleCatalog.Normalize(locale ?? LocaleContext.Current);
        IReadOnlyDictionary<string, object?> args = error.MessageArgs
            ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        string key = string.IsNullOrWhiteSpace(error.MessageKey)
            ? $"api.error.{error.Code}"
            : error.MessageKey!;
        string translated = TranslateNamed(key, "", args, normalized);
        if (!string.IsNullOrWhiteSpace(translated))
        {
            return translated;
        }

        string codeKey = $"api.error.{error.Code}";
        if (!string.Equals(codeKey, key, StringComparison.Ordinal))
        {
            translated = TranslateNamed(codeKey, "", args, normalized);
            if (!string.IsNullOrWhiteSpace(translated))
            {
                return translated;
            }
        }

        if (normalized == LocaleCatalog.DefaultLocale || !ContainsCjk(error.Message))
        {
            return error.Message;
        }

        return TranslateNamed(
            "api.error.internal_error",
            "The operation failed",
            locale: normalized);
    }

    /// <summary>将没有完整 OperationError 上下文的 CLI/MCP 边界消息收敛到同一套错误本地化规则。</summary>
    public static string TranslateUserMessage(string code, string message, string? locale = null)
    {
        string normalized = LocaleCatalog.Normalize(locale ?? LocaleContext.Current);
        if (normalized == LocaleCatalog.DefaultLocale)
        {
            return message;
        }

        return TranslateOperationError(
            new OperationError(
                code,
                message,
                OperationErrorKind.Validation,
                MessageKey: $"api.error.{code}"),
            normalized);
    }

    /// <summary>
    /// 旧 CLI 调用的兼容回退；新增 CLI 文案必须经由 cli.* 稳定 key 进入 TranslateNamed。
    /// 未迁移的旧调用在英文环境下也不会泄漏中文源文案。
    /// </summary>
    public static string TranslateCli(string code, string fallback, string? locale = null)
    {
        string normalized = LocaleCatalog.Normalize(locale ?? LocaleContext.Current);
        string translated = TranslateLegacy(fallback, normalized);
        if (normalized == LocaleCatalog.DefaultLocale || !ContainsCjk(translated))
        {
            return translated;
        }
        return code switch
        {
            "invalid_arguments" => "Invalid command-line arguments",
            "not_found" => "Not found",
            "ambiguous_target" => "Multiple matching objects found",
            "service_unavailable" => "The NexusPipeline service is unavailable",
            "timeout" => "The operation timed out",
            "cancelled" => "Cancelled",
            "execution_failed" => "Execution failed",
            "internal_error" => "The operation failed",
            "progress" => "Progress update",
            "diagnostic" => "Diagnostic information",
            _ => "The operation failed",
        };
    }

    /// <summary>
    /// 在宿主日志输出边界按 HostLocale 投影宿主生成的固定文字。
    /// 动态值仍由调用方拼接并保留原样；脚本、游戏和插件自行产生的日志内容不由这里翻译。
    /// </summary>
    public static string TranslateLog(string message, string? locale = null)
    {
        string normalized = LocaleCatalog.Normalize(locale ?? LocaleCatalog.HostLocale);
        if (normalized == LocaleCatalog.DefaultLocale || string.IsNullOrEmpty(message) || !ContainsCjk(message))
        {
            return message;
        }

        IReadOnlyDictionary<string, string> source = GetResources(LocaleCatalog.DefaultLocale);
        IReadOnlyDictionary<string, string> target = GetResources(normalized);
        string translated = message;
        var replacements = source
            .Where(item => item.Key.StartsWith("log.token.", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(item.Value)
                && !item.Value.Contains('{', StringComparison.Ordinal)
                && target.TryGetValue(item.Key, out string? value)
                && !string.IsNullOrWhiteSpace(value)
                && !string.Equals(item.Value, value, StringComparison.Ordinal))
            .Select(item => (Source: item.Value, Target: target[item.Key]))
            .Where(item => item.Source.Length >= 2)
            .OrderByDescending(item => item.Source.Length)
            .ThenBy(item => item.Source, StringComparer.Ordinal)
            .ToArray();

        foreach ((string sourceText, string targetText) in replacements)
        {
            translated = translated.Replace(sourceText, targetText, StringComparison.Ordinal);
        }

        translated = translated
            .Replace('：', ':')
            .Replace("，", ", ", StringComparison.Ordinal)
            .Replace(",  ", ", ", StringComparison.Ordinal)
            .Replace('。', '.')
            .Replace('；', ';')
            .Replace('（', '(')
            .Replace('）', ')')
            .Replace('「', '"')
            .Replace('」', '"')
            .Replace('、', ',');

        if (!ContainsCjk(translated))
        {
            return translated;
        }

        // 未登记的固定文案不能泄漏到其他语言日志；保留稳定诊断编号，便于切回默认语言或查找源码。
        string fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(message)))[..8].ToLowerInvariant();
        return TranslateNamed(
            "log.untranslated",
            "Host log event {code} contains untranslated host text",
            new Dictionary<string, object?> { ["code"] = fingerprint },
            normalized);
    }

    public static string TranslateLegacy(
        string fallback,
        string? locale = null,
        IReadOnlyList<object?>? args = null)
    {
        string key = fallback switch
        {
            "需要访问令牌（请求头 Authorization: Bearer <token>）" => "auth.required",
            "未找到" => "http.not_found",
            "请求方法不支持" => "http.method_not_allowed",
            "脚本名称不能为空" => "script.name_required",
            "用户名不能为空且不能包含非法字符" => "user.name_invalid",
            "缺少记录 ID" => "history.id_required",
            "记录不存在" => "history.not_found",
            "插件设置格式不正确" => "plugin.settings_invalid",
            "插件设置贡献不存在或插件未启用" => "plugin.contribution_not_found",
            "插件 UI 贡献不存在或插件未启用" => "plugin.ui_not_found",
            _ => "",
        };
        return Translate(key, fallback, locale, args);
    }

    public static string FormatNumber(double value, string? locale = null)
    {
        return value.ToString("N", LocaleCatalog.Culture(locale ?? LocaleContext.Current));
    }

    public static string FormatDate(DateTimeOffset value, string? locale = null)
    {
        return value.ToString("d", LocaleCatalog.Culture(locale ?? LocaleContext.Current));
    }

    public static string FormatTime(DateTimeOffset value, string? locale = null)
    {
        return value.ToString("t", LocaleCatalog.Culture(locale ?? LocaleContext.Current));
    }

    private static string Format(string template, IReadOnlyList<object?>? args)
    {
        if (args is null || args.Count == 0)
        {
            return template;
        }
        try
        {
            return string.Format(CultureInfo.InvariantCulture, template, args.ToArray());
        }
        catch (FormatException)
        {
            return template;
        }
    }

    private static bool TryGetTemplate(string locale, string key, out string? template)
    {
        IReadOnlyDictionary<string, string> resources = GetResources(locale);
        return resources.TryGetValue(key, out template);
    }

    private static IReadOnlyDictionary<string, string> GetResources(string locale)
    {
        lock (ResourceSync)
        {
            if (ResourceCache.TryGetValue(locale, out IReadOnlyDictionary<string, string>? cached))
            {
                return cached;
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                using Stream? stream = OpenResource(locale + ".json");
                if (stream is not null)
                {
                    Dictionary<string, string>? values = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
                    if (values is not null)
                    {
                        foreach ((string key, string value) in values)
                        {
                            result[key] = value;
                        }
                    }
                }
            }
            catch
            {
                // 内置 English 回退表和调用方 fallback 保证宿主在资源损坏时仍可用。
            }
            ResourceCache[locale] = result;
            return result;
        }
    }

    private static Stream? OpenResource(string fileName)
    {
        Assembly assembly = typeof(HostLocalization).Assembly;
        string suffix = $".Localization.Resources.{fileName}";
        string? resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        return resourceName is null ? null : assembly.GetManifestResourceStream(resourceName);
    }

    private static object? JsonScalar(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out string? text)) return text;
            if (value.TryGetValue<long>(out long integer)) return integer;
            if (value.TryGetValue<double>(out double number)) return number;
            if (value.TryGetValue<bool>(out bool boolean)) return boolean;
        }
        return node.ToJsonString(JsonOpts.Web);
    }

    private static bool ContainsCjk(string value)
    {
        return value.Any(character => character is >= '\u4e00' and <= '\u9fff');
    }
}
