using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
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
