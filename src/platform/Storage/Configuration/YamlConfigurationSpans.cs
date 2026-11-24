using NexusPipeline.Core.Configuration;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace NexusPipeline.Platform.Storage.Configuration;

internal sealed class YamlConfigurationSpans(ConfigurationUtf8Offsets source)
{
    private int _nodes;

    internal ConfigurationSpanNode Parse()
    {
        var parser = new Parser(new StringReader(source.Text));
        parser.Consume<StreamStart>();
        parser.Consume<DocumentStart>();
        var result = Read(parser, 0);
        parser.Consume<DocumentEnd>();
        parser.Consume<StreamEnd>();
        return result;
    }

    private ConfigurationSpanNode Read(IParser parser, int depth)
    {
        if (++_nodes > 8192 || depth > 32) throw new ConfigurationException("configuration_document_node_limit");
        var current = parser.Current ?? throw new ConfigurationException("configuration_document_empty");
        if (current is AnchorAlias || current is NodeEvent node && (!node.Anchor.IsEmpty || !node.Tag.IsEmpty))
            throw new ConfigurationException("configuration_yaml_reference_unsupported");
        if (current is Scalar scalar)
        {
            parser.MoveNext();
            var end = scalar.Style is ScalarStyle.Literal or ScalarStyle.Folded
                ? source.TrimBlockEnd(scalar.Start.Index, scalar.End.Index) : source.Byte(scalar.End.Index);
            return new(source.Byte(scalar.Start.Index), end, YamlConfigurationScalars.Read(scalar));
        }
        if (current is MappingStart mapping) return Mapping(parser, mapping, depth);
        if (current is SequenceStart sequence) return Sequence(parser, sequence, depth);
        throw new ConfigurationException("configuration_document_invalid");
    }

    private ConfigurationSpanNode Mapping(IParser parser, MappingStart start, int depth)
    {
        parser.MoveNext();
        var properties = new Dictionary<string, ConfigurationSpanNode>(StringComparer.Ordinal);
        var value = new JsonObject();
        var end = source.Byte(start.Start.Index);
        while (parser.Current is not MappingEnd)
        {
            if (parser.Current is not Scalar key || key.Value == "<<" || !key.Anchor.IsEmpty || !key.Tag.IsEmpty)
                throw new ConfigurationException("configuration_yaml_key_unsupported");
            if (System.Text.Encoding.UTF8.GetByteCount(key.Value) > 262144) throw new ConfigurationException("configuration_scalar_limit");
            parser.MoveNext();
            var child = Read(parser, depth + 1);
            if (!properties.TryAdd(key.Value, child)) throw new ConfigurationException("configuration_document_duplicate_key");
            value.Add(key.Value, child.Value);
            end = Math.Max(source.Byte(key.End.Index), child.End);
        }
        var finish = parser.Consume<MappingEnd>();
        if (start.Style == MappingStyle.Flow) end = source.FlowEnd(finish.End.Index, '}');
        return new(source.Byte(start.Start.Index), end, value, properties);
    }

    private ConfigurationSpanNode Sequence(IParser parser, SequenceStart start, int depth)
    {
        parser.MoveNext();
        var items = new List<ConfigurationSpanNode>();
        var value = new JsonArray();
        var end = source.Byte(start.Start.Index);
        while (parser.Current is not SequenceEnd)
        {
            var child = Read(parser, depth + 1);
            items.Add(child); value.Add(child.Value); end = child.End;
        }
        var finish = parser.Consume<SequenceEnd>();
        if (start.Style == SequenceStyle.Flow) end = source.FlowEnd(finish.End.Index, ']');
        return new(source.Byte(start.Start.Index), end, value, items: items);
    }
}

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

internal sealed class ConfigurationUtf8Offsets
{
    internal string Text { get; }
    private readonly int[] _offsets;

    internal ConfigurationUtf8Offsets(ReadOnlySpan<byte> bytes, int offset)
    {
        Text = new UTF8Encoding(false, true).GetString(bytes[offset..]);
        _offsets = new int[Text.Length + 1];
        var total = offset;
        for (var index = 0; index < Text.Length; index++)
        {
            _offsets[index] = total;
            var width = char.IsHighSurrogate(Text[index]) ? 2 : 1;
            total += Encoding.UTF8.GetByteCount(Text.AsSpan(index, width));
            if (width == 2) _offsets[++index] = -1;
        }
        _offsets[^1] = total;
    }

    internal int Byte(long character)
    {
        if (character < 0 || character >= _offsets.Length || _offsets[(int)character] < 0)
            throw new ConfigurationException("configuration_span_invalid");
        return _offsets[(int)character];
    }

    internal int TrimBlockEnd(long start, long end)
    {
        var result = checked((int)end);
        while (result > start && Text[result - 1] is ' ' or '\t' or '\r' or '\n') result--;
        return Byte(result);
    }

    internal int FlowEnd(long character, char closing)
    {
        // YamlDotNet's flow end event points at the closing delimiter, excluding that character.
        if (character < 0 || character >= Text.Length || Text[(int)character] != closing)
            throw new ConfigurationException("configuration_span_invalid");
        return Byte(character + 1);
    }
}
