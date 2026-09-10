using System.Globalization;
using NexusPipeline.Plugin.Abstractions;

namespace NexusPipeline.Plugins;

internal static class PluginLocalizationValidation
{
    public static bool IsValidText(PluginLocalizedText? text, int maxFallbackLength)
    {
        return text is null
            || IsSafeKey(text.Key, 128)
            && text.Fallback is not null
            && text.Fallback.Length <= maxFallbackLength;
    }

    public static bool IsValidValue(PluginLocalizedValue? value, int maxFallbackLength)
    {
        if (value is null)
        {
            return true;
        }
        if (!IsSafeKey(value.Key, 128)
            || value.Fallback is null
            || value.Fallback.Length > maxFallbackLength
            || value.Args is { Count: > 32 })
        {
            return false;
        }
        foreach ((string key, object? argument) in value.Args ?? new Dictionary<string, object?>())
        {
            if (!IsSafeKey(key, 64))
            {
                return false;
            }
            string rendered;
            try
            {
                rendered = Convert.ToString(argument, CultureInfo.InvariantCulture) ?? "";
            }
            catch
            {
                return false;
            }
            if (rendered.Length > 2048 || rendered.Any(char.IsControl))
            {
                return false;
            }
        }
        return true;
    }

    internal static bool IsSafeKey(string? value, int maxLength)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.');
    }
}
