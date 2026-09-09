using System.Globalization;

namespace NexusPipeline.Localization;

/// <summary>宿主协议错误和基础 UI 文案的稳定翻译表。业务模块可继续逐步迁移到语义 key。</summary>
internal static class HostLocalization
{
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

    public static string Translate(
        string? key,
        string fallback,
        string? locale = null,
        IReadOnlyList<object?>? args = null)
    {
        string normalized = LocaleCatalog.Normalize(locale ?? LocaleContext.Current);
        if (normalized == LocaleCatalog.DefaultLocale || string.IsNullOrWhiteSpace(key))
        {
            return fallback;
        }
        if (!English.TryGetValue(key!, out string? template))
        {
            return fallback;
        }
        return Format(template, args);
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
}
