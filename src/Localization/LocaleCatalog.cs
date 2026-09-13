using System.Collections.Specialized;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace NexusPipeline.Localization;

/// <summary>宿主支持的语言注册表。请求输入只参与选择已注册的语言。</summary>
internal static class LocaleCatalog
{
    public const string DefaultLocale = "zh-CN";
    public const string EnglishLocale = "en-US";

    private sealed record Registry(string DefaultLocale, IReadOnlyList<string> SupportedLocales);

    private static readonly Registry LocaleRegistry = LoadRegistry();

    public static IReadOnlyList<string> SupportedLocales => LocaleRegistry.SupportedLocales;

    private static string _hostLocale = DefaultLocale;

    /// <summary>后台任务、CLI、托盘、通知和宿主日志使用的全局语言。</summary>
    public static string HostLocale => _hostLocale;

    public static void SetHostLocale(string? locale)
    {
        _hostLocale = Normalize(locale);
    }

    public static string Normalize(string? value)
    {
        string candidate = value?.Trim() ?? "";
        if (TryResolve(candidate, out string? resolved))
        {
            return resolved;
        }
        return LocaleRegistry.DefaultLocale;
    }

    public static bool TryResolve(string? value, out string locale)
    {
        string candidate = NormalizeTag(value);
        if (candidate.Length > 0)
        {
            string? exact = LocaleRegistry.SupportedLocales.FirstOrDefault(
                item => string.Equals(NormalizeTag(item), candidate, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                locale = exact;
                return true;
            }

            string language = candidate.Split('-', 2)[0];
            string? languageMatch = LocaleRegistry.SupportedLocales.FirstOrDefault(
                item => string.Equals(
                    NormalizeTag(item).Split('-', 2)[0],
                    language,
                    StringComparison.OrdinalIgnoreCase));
            if (languageMatch is not null)
            {
                locale = languageMatch;
                return true;
            }
        }
        locale = LocaleRegistry.DefaultLocale;
        return false;
    }

    public static string Resolve(NameValueCollection headers)
    {
        return Resolve(headers["X-Nexus-Locale"], headers["Accept-Language"]);
    }

    public static string Resolve(string? explicitLocale, string? acceptLanguage)
    {
        if (!string.IsNullOrWhiteSpace(explicitLocale))
        {
            return Normalize(explicitLocale);
        }

        if (string.IsNullOrWhiteSpace(acceptLanguage))
        {
            return LocaleRegistry.DefaultLocale;
        }

        foreach (string item in acceptLanguage.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string language = item.Split(';', 2)[0].Trim();
            if (TryResolve(language, out string? resolved))
            {
                return resolved;
            }
        }
        return LocaleRegistry.DefaultLocale;
    }

    public static CultureInfo Culture(string? locale)
    {
        return CultureInfo.GetCultureInfo(Normalize(locale));
    }

    private static string NormalizeTag(string? value)
    {
        return (value ?? "").Trim().Replace('_', '-');
    }

    private static Registry LoadRegistry()
    {
        try
        {
            using Stream? stream = OpenResource("locales.json");
            if (stream is not null)
            {
                using JsonDocument document = JsonDocument.Parse(stream);
                JsonElement root = document.RootElement;
                string defaultLocale = root.TryGetProperty("default", out JsonElement defaultNode)
                    ? NormalizeTag(defaultNode.GetString())
                    : DefaultLocale;
                var supported = root.TryGetProperty("supported", out JsonElement supportedNode)
                    && supportedNode.ValueKind == JsonValueKind.Array
                    ? supportedNode.EnumerateArray()
                        .Select(item => item.ValueKind == JsonValueKind.String
                            ? NormalizeTag(item.GetString())
                            : item.TryGetProperty("id", out JsonElement idNode) ? NormalizeTag(idNode.GetString()) : "")
                        .Where(item => item.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                    : Array.Empty<string>();
                if (supported.Length > 0)
                {
                    if (!supported.Contains(defaultLocale, StringComparer.OrdinalIgnoreCase))
                    {
                        defaultLocale = supported[0];
                    }
                    return new Registry(defaultLocale, supported);
                }
            }
        }
        catch
        {
            // 内置默认注册表保证设置、CLI 和测试在资源损坏时仍可启动。
        }

        return new Registry(DefaultLocale, new[] { DefaultLocale, EnglishLocale });
    }

    private static Stream? OpenResource(string fileName)
    {
        Assembly assembly = typeof(LocaleCatalog).Assembly;
        string suffix = $".Localization.Resources.{fileName}";
        string? resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        return resourceName is null ? null : assembly.GetManifestResourceStream(resourceName);
    }
}

/// <summary>请求与后台任务之间传递的语言上下文。Web 请求在路由入口建立作用域。</summary>
internal static class LocaleContext
{
    private static readonly AsyncLocal<string?> CurrentValue = new();

    public static string Current => LocaleCatalog.Normalize(CurrentValue.Value ?? LocaleCatalog.HostLocale);

    public static IDisposable Push(string? locale)
    {
        string? previous = CurrentValue.Value;
        CurrentValue.Value = LocaleCatalog.Normalize(locale);
        return new Scope(() => CurrentValue.Value = previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly Action _restore;
        private int _disposed;

        public Scope(Action restore) => _restore = restore;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _restore();
            }
        }
    }
}
