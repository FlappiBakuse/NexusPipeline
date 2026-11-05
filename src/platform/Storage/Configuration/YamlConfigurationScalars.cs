using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NexusPipeline.Core.Configuration;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace NexusPipeline.Platform.Storage.Configuration;

internal static class YamlConfigurationScalars
{
    private static readonly Regex Integer = new("^[+-]?[0-9]+$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Floating = new("^[+-]?(?:[0-9]+(?:\\.[0-9]*)?|\\.[0-9]+)(?:[eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static JsonNode? Read(Scalar scalar)
    {
        if (Encoding.UTF8.GetByteCount(scalar.Value) > 262144) throw new ConfigurationException("configuration_scalar_limit");
        var text = scalar.Value;
        if (scalar.Style != ScalarStyle.Plain) return JsonValue.Create(text);
        if (text is "" or "~" or "null" or "Null" or "NULL") return null;
        if (text is "true" or "True" or "TRUE") return JsonValue.Create(true);
        if (text is "false" or "False" or "FALSE") return JsonValue.Create(false);
        if (text.ToLowerInvariant() is ".nan" or ".inf" or "+.inf" or "-.inf") throw new ConfigurationException("configuration_number_invalid");
        if (text.StartsWith("0x", StringComparison.Ordinal) && text.Length > 2 && text[2..].All(Uri.IsHexDigit))
        {
            if (text.Length > 512) throw new ConfigurationException("configuration_number_invalid");
            return Number(BigInteger.Parse("0" + text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
        }
        if (text.StartsWith("0o", StringComparison.Ordinal) && text.Length > 2 && text[2..].All(character => character is >= '0' and <= '7'))
        {
            if (text.Length > 512) throw new ConfigurationException("configuration_number_invalid");
            var value = BigInteger.Zero;
            foreach (var character in text[2..]) value = value * 8 + (character - '0');
            return Number(value.ToString(CultureInfo.InvariantCulture));
        }
        if (Integer.IsMatch(text))
        {
            if (text.Length > 512) throw new ConfigurationException("configuration_number_invalid");
            return Number(BigInteger.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
        }
        if (!Floating.IsMatch(text)) return JsonValue.Create(text);
        if (text.Length > 512) throw new ConfigurationException("configuration_number_invalid");
        var parts = text.TrimStart('+').Split(['e', 'E'], 2);
        var mantissa = parts[0];
        if (mantissa.StartsWith('.')) mantissa = "0" + mantissa;
        if (mantissa.StartsWith("-.", StringComparison.Ordinal)) mantissa = "-0" + mantissa[1..];
        if (mantissa.EndsWith('.')) mantissa += "0";
        var sign = mantissa.StartsWith('-') ? "-" : "";
        var digits = mantissa.TrimStart('-').TrimStart('0');
        if (digits.Length == 0 || digits.StartsWith('.')) digits = "0" + digits;
        return Number(sign + digits + (parts.Length == 2 ? "e" + parts[1] : ""));
    }

    private static JsonNode Number(string text)
    {
        using var document = JsonDocument.Parse(text);
        if (!document.RootElement.TryGetDouble(out var value) || !double.IsFinite(value)) throw new ConfigurationException("configuration_number_invalid");
        return JsonValue.Create(document.RootElement.Clone())!;
    }
}
