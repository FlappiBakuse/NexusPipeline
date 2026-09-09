using System.Collections.Specialized;
using System.Globalization;

namespace NexusPipeline.Localization;

/// <summary>宿主支持的有限语言集合。请求输入只参与选择白名单语言。</summary>
internal static class LocaleCatalog
{
    public const string DefaultLocale = "zh-CN";
    public const string EnglishLocale = "en-US";

    public static IReadOnlyList<string> SupportedLocales { get; } =
        new[] { DefaultLocale, EnglishLocale };

    public static string Normalize(string? value)
    {
        string candidate = value?.Trim() ?? "";
        if (IsLanguage(candidate, "zh"))
        {
            return DefaultLocale;
        }
        if (IsLanguage(candidate, "en"))
        {
            return EnglishLocale;
        }
        return DefaultLocale;
    }

    public static string Resolve(NameValueCollection headers)
    {
        string? explicitLocale = headers["X-Nexus-Locale"];
        if (!string.IsNullOrWhiteSpace(explicitLocale))
        {
            return Normalize(explicitLocale);
        }

        string? acceptLanguage = headers["Accept-Language"];
        if (string.IsNullOrWhiteSpace(acceptLanguage))
        {
            return DefaultLocale;
        }

        foreach (string item in acceptLanguage.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string language = item.Split(';', 2)[0].Trim();
            if (IsLanguage(language, "en"))
            {
                return EnglishLocale;
            }
            if (IsLanguage(language, "zh"))
            {
                return DefaultLocale;
            }
        }
        return DefaultLocale;
    }

    public static CultureInfo Culture(string? locale)
    {
        return CultureInfo.GetCultureInfo(Normalize(locale));
    }

    private static bool IsLanguage(string value, string language)
    {
        return value.Equals(language, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>请求与后台任务之间传递的语言上下文。Web 请求在路由入口建立作用域。</summary>
internal static class LocaleContext
{
    private static readonly AsyncLocal<string?> CurrentValue = new();

    public static string Current => LocaleCatalog.Normalize(CurrentValue.Value);

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
