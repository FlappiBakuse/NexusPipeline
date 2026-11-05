using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Configuration;

internal static class JsonConfigurationSpans
{
    internal static ConfigurationSpanNode Parse(ReadOnlySpan<byte> source, int offset, bool comments)
    {
        var reader = new Utf8JsonReader(source[offset..], new JsonReaderOptions
            { MaxDepth = 32, AllowTrailingCommas = comments, CommentHandling = comments ? JsonCommentHandling.Skip : JsonCommentHandling.Disallow });
        if (!reader.Read()) throw new ConfigurationException("configuration_document_empty");
        var count = 0;
        var root = Read(ref reader, offset, ref count);
        if (reader.Read()) throw new ConfigurationException("configuration_document_invalid");
        return root;
    }

    private static ConfigurationSpanNode Read(ref Utf8JsonReader reader, int offset, ref int count)
    {
        if (++count > 8192) throw new ConfigurationException("configuration_document_node_limit");
        var start = checked(offset + (int)reader.TokenStartIndex);
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var properties = new Dictionary<string, ConfigurationSpanNode>(StringComparer.Ordinal);
            var value = new JsonObject();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) throw new ConfigurationException("configuration_document_invalid");
                if (reader.ValueSpan.Length > 262144) throw new ConfigurationException("configuration_scalar_limit");
                var name = reader.GetString()!;
                if (!reader.Read()) throw new ConfigurationException("configuration_document_invalid");
                var node = Read(ref reader, offset, ref count);
                if (!properties.TryAdd(name, node)) throw new ConfigurationException("configuration_document_duplicate_key");
                value.Add(name, node.Value);
            }
            if (reader.TokenType != JsonTokenType.EndObject) throw new ConfigurationException("configuration_document_invalid");
            return new(start, checked(offset + (int)reader.BytesConsumed), value, properties);
        }
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var items = new List<ConfigurationSpanNode>();
            var value = new JsonArray();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                var node = Read(ref reader, offset, ref count);
                items.Add(node); value.Add(node.Value);
            }
            if (reader.TokenType != JsonTokenType.EndArray) throw new ConfigurationException("configuration_document_invalid");
            return new(start, checked(offset + (int)reader.BytesConsumed), value, items: items);
        }
        JsonNode? scalar = reader.TokenType switch
        {
            JsonTokenType.String => JsonValue.Create(reader.GetString()),
            JsonTokenType.True => JsonValue.Create(true), JsonTokenType.False => JsonValue.Create(false),
            JsonTokenType.Null => null, JsonTokenType.Number => Number(ref reader),
            _ => throw new ConfigurationException("configuration_document_invalid")
        };
        if (reader.HasValueSequence || reader.ValueSpan.Length > 262144) throw new ConfigurationException("configuration_scalar_limit");
        return new(start, checked(offset + (int)reader.BytesConsumed), scalar);
    }

    private static JsonNode Number(ref Utf8JsonReader reader)
    {
        if (!reader.TryGetDouble(out var number) || !double.IsFinite(number)) throw new ConfigurationException("configuration_number_invalid");
        using var value = JsonDocument.ParseValue(ref reader);
        return JsonValue.Create(value.RootElement.Clone())!;
    }
}
