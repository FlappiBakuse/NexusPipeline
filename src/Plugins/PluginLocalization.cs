using System.Globalization;
using System.Text.Json.Nodes;
using NexusPipeline.Localization;
using NexusPipeline.Plugin.Abstractions;

namespace NexusPipeline.Plugins;

/// <summary>插件 manifest 声明的本地化资源。资源文件在加载时完整校验并读取，运行期间不暴露插件目录。</summary>
internal sealed class PluginLocalizationManifest
{
    private const int MaxResourceBytes = 512 * 1024;
    private const int MaxKeys = 4096;
    private const int MaxKeyLength = 128;
    private const int MaxValueLength = 8192;

    private PluginLocalizationManifest(
        string defaultLocale,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> resources)
    {
        DefaultLocale = defaultLocale;
        Resources = resources;
    }

    public string DefaultLocale { get; }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Resources { get; }

    public static PluginLocalizationManifest Empty { get; } =
        new(LocaleCatalog.DefaultLocale, new Dictionary<string, IReadOnlyDictionary<string, string>>());

    public static bool TryLoad(
        JsonObject root,
        string pluginDirectory,
        out PluginLocalizationManifest localization,
        out string? error)
    {
        localization = Empty;
        error = null;
        JsonNode? node = root["localization"];
        if (node is null)
        {
            return true;
        }
        if (node is not JsonObject localizationNode)
        {
            error = "localization 必须是 JSON 对象";
            return false;
        }

        string defaultLocale = localizationNode["defaultLocale"]?.ToString()?.Trim() ?? LocaleCatalog.DefaultLocale;
        if (!TryCanonicalLocale(defaultLocale, out defaultLocale))
        {
            error = "localization.defaultLocale 不受支持";
            return false;
        }
        JsonNode? localeNode = localizationNode["locales"] ?? localizationNode["resources"];
        if (localeNode is null)
        {
            error = "localization 缺少 locales 对象";
            return false;
        }
        if (localeNode is not JsonObject localeObject)
        {
            error = "localization.locales 必须是 JSON 对象";
            return false;
        }
        var resources = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string localeName, JsonNode? pathNode) in localeObject)
        {
            if (!TryCanonicalLocale(localeName, out string canonicalLocale)
                || pathNode is null
                || pathNode.ToString().Length > 256)
            {
                error = $"localization locale 或资源路径无效：{localeName}";
                return false;
            }
            if (resources.ContainsKey(canonicalLocale))
            {
                error = $"localization locale 重复：{canonicalLocale}";
                return false;
            }
            string relativePath = pathNode.ToString().Trim();
            if (!IsSafeResourcePath(relativePath))
            {
                error = $"localization 资源路径无效：{relativePath}";
                return false;
            }
            string rootPath = Path.GetFullPath(pluginDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string resourcePath = Path.GetFullPath(Path.Combine(pluginDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!resourcePath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(resourcePath))
            {
                error = $"localization 资源文件不存在或超出插件目录：{relativePath}";
                return false;
            }
            FileInfo info = new(resourcePath);
            if (info.Length > MaxResourceBytes)
            {
                error = $"localization 资源文件过大：{relativePath}";
                return false;
            }
            if (!TryReadResource(resourcePath, out IReadOnlyDictionary<string, string>? values, out error))
            {
                return false;
            }
            resources[canonicalLocale] = values;
        }
        if (resources.Count > 0 && !resources.ContainsKey(defaultLocale))
        {
            error = "localization.defaultLocale 必须存在对应资源";
            return false;
        }
        localization = new PluginLocalizationManifest(defaultLocale, resources);
        return true;
    }

    public string Resolve(
        string locale,
        string key,
        string fallback,
        IReadOnlyDictionary<string, object?>? args = null)
    {
        string canonicalLocale = LocaleCatalog.Normalize(locale);
        string? value = Find(canonicalLocale, key) ?? Find(DefaultLocale, key);
        return Format(value ?? fallback, args);
    }

    private string? Find(string locale, string key)
    {
        return Resources.TryGetValue(locale, out IReadOnlyDictionary<string, string>? values)
            && values.TryGetValue(key, out string? value)
            ? value
            : null;
    }

    private static bool TryReadResource(
        string path,
        out IReadOnlyDictionary<string, string> values,
        out string? error)
    {
        values = new Dictionary<string, string>(StringComparer.Ordinal);
        error = null;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
            {
                error = "localization 资源必须是 JSON 对象";
                return false;
            }
            if (root.Count > MaxKeys)
            {
                error = $"localization key 数量超过上限（{MaxKeys}）";
                return false;
            }
            var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach ((string key, JsonNode? node) in root)
            {
                if (node is not JsonValue jsonValue
                    || !jsonValue.TryGetValue<string>(out string? value)
                    || value is null)
                {
                    error = "localization value 必须是字符串";
                    return false;
                }
                if (key.Length is < 1 or > MaxKeyLength
                    || value.Length > MaxValueLength
                    || key.Any(char.IsControl)
                    || key.Any(char.IsWhiteSpace)
                    || value.Any(char.IsControl))
                {
                    error = "localization key 或 value 超出范围";
                    return false;
                }
                parsed[key] = value;
            }
            values = parsed;
            return true;
        }
        catch (Exception ex)
        {
            error = $"localization 资源解析失败：{ex.Message}";
            return false;
        }
    }

    private static bool IsSafeResourcePath(string value)
    {
        return value.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && value.StartsWith("i18n/", StringComparison.OrdinalIgnoreCase)
            && !value.Contains('\\', StringComparison.Ordinal)
            && !value.Contains(':', StringComparison.Ordinal)
            && !value.Split('/').Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."
                || !segment.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.'));
    }

    private static bool TryCanonicalLocale(string value, out string canonical)
    {
        string candidate = value.Trim();
        canonical = candidate.Equals("en", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith("en-", StringComparison.OrdinalIgnoreCase)
            ? LocaleCatalog.EnglishLocale
            : candidate.Equals("zh", StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith("zh-", StringComparison.OrdinalIgnoreCase)
                ? LocaleCatalog.DefaultLocale
                : "";
        return canonical.Length > 0;
    }

    private static string Format(string value, IReadOnlyDictionary<string, object?>? args)
    {
        if (args is null || args.Count == 0)
        {
            return value;
        }
        foreach ((string key, object? arg) in args)
        {
            value = value.Replace("{" + key + "}", Convert.ToString(arg, CultureInfo.InvariantCulture) ?? "", StringComparison.Ordinal);
        }
        return value;
    }
}

internal sealed class PluginLocalizationService : IPluginLocalization
{
    private readonly PluginLocalizationManifest _manifest;

    public PluginLocalizationService(PluginLocalizationManifest manifest)
    {
        _manifest = manifest;
    }

    public string Locale => LocaleContext.Current;

    public string DefaultLocale => _manifest.DefaultLocale;

    public string T(string key, string fallback = "", IReadOnlyDictionary<string, object?>? args = null)
    {
        return _manifest.Resolve(Locale, key, fallback, args);
    }

    public string FormatNumber(double value) => HostLocalization.FormatNumber(value, Locale);

    public string FormatDate(DateTimeOffset value) => HostLocalization.FormatDate(value, Locale);

    public string FormatTime(DateTimeOffset value) => HostLocalization.FormatTime(value, Locale);
}

internal static class PluginLocalizedTextResolver
{
    public static string Resolve(
        PluginLocalizedText? text,
        string fallback,
        PluginLocalizationManifest? manifest = null,
        string? locale = null)
    {
        if (text is null || manifest is null)
        {
            return fallback;
        }
        return manifest.Resolve(locale ?? LocaleContext.Current, text.Key, text.Fallback.Length > 0 ? text.Fallback : fallback);
    }

    public static string Resolve(
        PluginLocalizedValue? value,
        string fallback,
        PluginLocalizationManifest? manifest = null,
        string? locale = null)
    {
        if (value is null || manifest is null)
        {
            return fallback;
        }
        return manifest.Resolve(locale ?? LocaleContext.Current, value.Key, value.Fallback.Length > 0 ? value.Fallback : fallback, value.Args);
    }
}
