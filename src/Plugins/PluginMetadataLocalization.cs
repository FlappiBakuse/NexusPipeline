using NexusPipeline.Localization;

namespace NexusPipeline.Plugins;

internal static class PluginMetadataLocalization
{
    public static PluginLocalizedMetadata? Resolve(
        IReadOnlyDictionary<string, PluginLocalizedMetadata> locales,
        string? locale)
    {
        string normalized = LocaleCatalog.Normalize(locale);
        return locales.TryGetValue(normalized, out PluginLocalizedMetadata? value) ? value : null;
    }

    public static string DisplayName(
        IReadOnlyDictionary<string, PluginLocalizedMetadata> locales,
        string fallback,
        string? locale) => Resolve(locales, locale)?.DisplayName ?? fallback;

    public static string GameName(
        IReadOnlyDictionary<string, PluginLocalizedMetadata> locales,
        string fallback,
        string? locale) => Resolve(locales, locale)?.GameName ?? fallback;

    public static string Description(
        IReadOnlyDictionary<string, PluginLocalizedMetadata> locales,
        string fallback,
        string? locale) => Resolve(locales, locale)?.Description ?? fallback;

    public static IReadOnlyList<string> Tags(
        IReadOnlyDictionary<string, PluginLocalizedMetadata> locales,
        IReadOnlyList<string> fallback,
        string? locale) => Resolve(locales, locale)?.Tags ?? fallback;

    public static IReadOnlyList<PluginChangelogEntry> Changelog(
        IReadOnlyDictionary<string, PluginLocalizedMetadata> locales,
        IReadOnlyList<PluginChangelogEntry> fallback,
        string? locale) => Resolve(locales, locale)?.Changelog ?? fallback;
}
