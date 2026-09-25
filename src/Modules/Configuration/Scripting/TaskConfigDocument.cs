using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace NexusPipeline.Modules.Configuration.Scripting;

internal sealed record TaskConfigOperation(JsonArray Selector, JsonNode? Expected, JsonNode? Value, string Purpose);

/// <summary>Safe data tree plus original token spans. Edits preserve all bytes outside selected values.</summary>
internal sealed class TaskConfigDocument
{
    private readonly string _text;
    private readonly bool _bom;
    private readonly Node _root;
    internal string Format { get; }
    internal JsonNode? Document => _root.Value?.DeepClone();
    internal bool ContainsTopLevelProperty(string name) => _root.Properties?.ContainsKey(name) == true;

    private sealed record Node(JsonNode? Value, int Start, int End,
        Dictionary<string, Node>? Properties = null, List<Node>? Items = null);

    internal TaskConfigDocument(byte[] bytes, string format)
    {
        if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("resource_limit: config exceeds 2 MiB");
        if (format is not ("json" or "yaml")) throw new InvalidDataException("unsupported_schema: document format");
        Format = format;
        _bom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
        _text = new UTF8Encoding(false, true).GetString(bytes.AsSpan(_bom ? 3 : 0));
        if (format == "json")
        {
            using var json = JsonDocument.Parse(_text, new JsonDocumentOptions { MaxDepth = 32 });
        }
        var parser = new Parser(new StringReader(_text));
        parser.Consume<StreamStart>();
        parser.Consume<DocumentStart>();
        _root = Read(parser, 0);
        parser.Consume<DocumentEnd>();
        parser.Consume<StreamEnd>(); // multiple documents are intentionally unsupported
    }

    private static Node Read(IParser parser, int depth)
    {
        if (depth > 32) throw new InvalidDataException("resource_limit: config depth");
        var current = parser.Current ?? throw new InvalidDataException("unsupported_schema: empty YAML");
        if (current is AnchorAlias || current is NodeEvent n && (!n.Anchor.IsEmpty || !n.Tag.IsEmpty))
            throw new InvalidDataException("unsupported_schema: YAML anchors, aliases and tags are not supported");
        if (current is Scalar scalar)
        {
            parser.MoveNext();
            JsonNode? value = JsonValue.Create(scalar.Value);
            if (scalar.Style == ScalarStyle.Plain)
            {
                if (scalar.Value is "null" or "Null" or "NULL" or "~" or "") value = null;
                else if (bool.TryParse(scalar.Value, out bool flag)) value = JsonValue.Create(flag);
                else if (scalar.Value.Length > 0 && (char.IsDigit(scalar.Value[0]) || scalar.Value[0] == '-'))
                {
                    try
                    {
                        var number = JsonNode.Parse(scalar.Value);
                        if (number is JsonValue && number.GetValueKind() == JsonValueKind.Number) value = number;
                    }
                    catch (JsonException) { /* dates/identifiers remain strings; bytes remain untouched */ }
                }
            }
            return new(value, checked((int)scalar.Start.Index), checked((int)scalar.End.Index));
        }
        if (current is MappingStart mapping)
        {
            parser.MoveNext();
            var obj = new JsonObject();
            var children = new Dictionary<string, Node>(StringComparer.Ordinal);
            while (parser.Current is not MappingEnd)
            {
                if (parser.Current is not Scalar key || key.Value == "<<" || !key.Anchor.IsEmpty || !key.Tag.IsEmpty)
                    throw new InvalidDataException("unsupported_schema: YAML mapping key");
                parser.MoveNext();
                Node child = Read(parser, depth + 1);
                if (!children.TryAdd(key.Value, child)) throw new InvalidDataException("unsupported_schema: duplicate key");
                obj.Add(key.Value, child.Value?.DeepClone());
            }
            var end = parser.Consume<MappingEnd>();
            return new(obj, checked((int)mapping.Start.Index), checked((int)end.End.Index + (mapping.Style == MappingStyle.Flow ? 1 : 0)), children);
        }
        if (current is SequenceStart sequence)
        {
            parser.MoveNext();
            var children = new List<Node>();
            var array = new JsonArray();
            while (parser.Current is not SequenceEnd)
            {
                Node child = Read(parser, depth + 1);
                children.Add(child); array.Add(child.Value?.DeepClone());
            }
            var end = parser.Consume<SequenceEnd>();
            return new(array, checked((int)sequence.Start.Index), checked((int)end.End.Index + (sequence.Style == SequenceStyle.Flow ? 1 : 0)), Items: children);
        }
        throw new InvalidDataException("unsupported_schema: YAML node");
    }

    private Node Select(JsonArray selector)
    {
        if (selector.Count is 0 or > 32) throw new InvalidDataException("invalid selector length");
        Node node = _root;
        foreach (var token in selector)
        {
            if (token is JsonValue property && property.TryGetValue<string>(out var name))
            {
                if (node.Properties is null || !node.Properties.TryGetValue(name, out var next))
                    throw new InvalidDataException("selector property missing");
                node = next;
            }
            else if (token is JsonObject match && node.Items is {} items)
            {
                if (match.Count == 2 && match["by"] is JsonValue by && by.TryGetValue<string>(out string? key) && match.ContainsKey("value"))
                {
                    var candidates = items.Where(item => item.Properties is {} props && props.TryGetValue(key, out var id) && JsonNode.DeepEquals(id.Value, match["value"])).ToArray();
                    if (candidates.Length != 1) throw new InvalidDataException("ambiguous selector identity");
                    node = candidates[0];
                }
                else if (match.Count == 3 && match["index"] is JsonValue index && index.TryGetValue<int>(out int i)
                    && match["guardKey"] is JsonValue guard && guard.TryGetValue<string>(out string? guardKey) && match.ContainsKey("guardValue"))
                {
                    if (i < 0 || i >= items.Count || items[i].Properties is not {} props || !props.TryGetValue(guardKey, out var id)
                        || !JsonNode.DeepEquals(id.Value, match["guardValue"])) throw new InvalidDataException("selector identity guard failed");
                    node = items[i];
                }
                else throw new InvalidDataException("unsupported selector");
            }
            else throw new InvalidDataException("unsupported selector");
        }
        return node;
    }

    internal JsonNode? ReadSelection(JsonArray selector) => Select(selector).Value?.DeepClone();

    internal byte[] Patch(IReadOnlyList<TaskConfigOperation> operations, IReadOnlySet<string> allowedSelectors)
    {
        if (operations.Count is 0 or > 2048) throw new InvalidDataException("resource_limit: patch operations");
        var edits = new List<(int Start, int End, string Value)>();
        foreach (var operation in operations)
        {
            if (operation.Purpose is not ("selection" or "cursor" or "repair") || !allowedSelectors.Contains(operation.Selector.ToJsonString()))
                throw new InvalidDataException("patch field was not authorized by original discovery");
            Node selected = Select(operation.Selector);
            if (!JsonNode.DeepEquals(selected.Value, operation.Expected)) throw new InvalidDataException("configuration_conflict: expected value");
            if (!AllowedValue(operation.Value) || !AllowedValue(operation.Expected)) throw new InvalidDataException("patch value must be boolean/string or a flat list");
            // Block YAML sequences include indentation/newlines in their span; changing these
            // cannot be proven local with this codec. Individual scalar leaves remain supported.
            string original = _text[selected.Start..selected.End];
            if (Format == "yaml" && (original.Contains('\n') || selected.Items is not null && !original.TrimStart().StartsWith('[')))
                throw new InvalidDataException("unsupported_schema: patch requires a scalar or flow sequence");
            edits.Add((selected.Start, selected.End, operation.Value!.ToJsonString()));
        }
        edits.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (int i = 1; i < edits.Count; i++) if (edits[i].Start < edits[i - 1].End) throw new InvalidDataException("overlapping patch operations");
        string result = _text;
        foreach (var edit in edits.AsEnumerable().Reverse()) result = result[..edit.Start] + edit.Value + result[edit.End..];
        byte[] payload = new UTF8Encoding(false).GetBytes((_bom ? "\uFEFF" : "") + result);
        var verified = new TaskConfigDocument(payload, Format);
        // Verify the complete semantic projection against the original with only selected nodes replaced.
        JsonNode? expected = Document;
        foreach (var operation in operations)
        {
            Node originalNode = Select(operation.Selector);
            ReplaceByPath(_root, originalNode, expected, operation.Value);
        }
        if (!JsonNode.DeepEquals(expected, verified.Document)) throw new InvalidDataException("patch round-trip changed unrelated semantics");
        return payload;
    }

    private static bool ReplaceByPath(Node tree, Node target, JsonNode? copy, JsonNode? value)
    {
        if (tree.Properties is {} properties && copy is JsonObject obj)
            foreach (var (key, child) in properties)
            {
                if (ReferenceEquals(child, target)) { obj[key] = value?.DeepClone(); return true; }
                if (ReplaceByPath(child, target, obj[key], value)) return true;
            }
        if (tree.Items is {} items && copy is JsonArray array)
            for (int i = 0; i < items.Count; i++)
            {
                if (ReferenceEquals(items[i], target)) { array[i] = value?.DeepClone(); return true; }
                if (ReplaceByPath(items[i], target, array[i], value)) return true;
            }
        return false;
    }

    private static bool AllowedValue(JsonNode? value) => value switch
    {
        JsonValue v => v.TryGetValue<bool>(out _) || v.TryGetValue<string>(out string? s) && s.Length <= 512,
        JsonArray a => a.Count <= 1024 && a.All(item => item is JsonValue && AllowedValue(item)),
        _ => false,
    };
}
