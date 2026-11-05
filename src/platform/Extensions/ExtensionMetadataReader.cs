using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using NexusPipeline.Core.Extensions;
using WireMetadata = NexusPipeline.Extensibility.Protocol.ExtensionMetadata;

namespace NexusPipeline.Platform.Extensions;

internal static class ExtensionMetadataReader
{
    internal static ExtensionMetadata? Read(WireMetadata? value)
    {
        if (value is null) return null;
        var locales = value.Locales.ToImmutableDictionary(pair => pair.Key, pair => new ExtensionLocaleText(
            pair.Value.GetProperty("displayName").GetString()!, pair.Value.GetProperty("gameName").GetString()!,
            pair.Value.GetProperty("summary").GetString()!), StringComparer.Ordinal);
        if (!locales.TryGetValue(value.DefaultLocale, out var fallback) || fallback.DisplayName != value.DisplayName ||
            fallback.GameName != value.GameName || fallback.Summary != value.Summary)
            throw new JsonException("extension_localization_default_unbound");
        return new(value.DefaultLocale, value.DisplayName, value.GameName, value.Summary, value.Author,
            value.Tags.ToImmutableArray(), locales, Date(value.CreatedAt), Date(value.UpdatedAt));
    }

    private static DateTimeOffset? Date(string? value)
    {
        if (value is null) return null;
        var parsed = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.None);
        if (parsed.Offset != TimeSpan.Zero) throw new JsonException("extension_metadata_date_invalid");
        return parsed;
    }
}
