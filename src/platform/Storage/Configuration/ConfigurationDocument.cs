using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using YamlDotNet.Core;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed class ConfigurationDocument : IDisposable, IConfigurationValueReader
{
    private readonly object _gate = new();
    private readonly ConfigurationPatchBinding _binding;
    private readonly ConfigurationSyntax _syntax;
    private readonly ConfigurationDocumentAccess _access;
    private byte[]? _source;
    private ConfigurationSpanNode? _root;
    public ConfigurationPatchBinding Binding => _binding;
    public string ResourceId => _binding.ResourceId;
    public long Revision => _binding.Revision;
    public string SourceSha256 => _binding.SourceSha256;
    public bool IsStructured => _syntax != ConfigurationSyntax.Opaque;

    public void Validate()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_source is null, this);
            if (IsStructured) _ = Root();
        }
    }

    public ConfigurationDocument(ConfigurationPatchBinding binding, ConfigurationSyntax syntax, ConfigurationDocumentAccess access,
        ReadOnlyMemory<byte> source)
    {
        ConfigurationPatchPolicy.ValidateBinding(binding);
        if (!Enum.IsDefined(syntax) || source.Length > StrictJson.MaximumBytes) throw new ConfigurationException("configuration_document_limit");
        _binding = binding; _syntax = syntax; _access = ConfigurationPatchPolicy.Freeze(access);
        var bytes = source.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != binding.SourceSha256)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new ConfigurationException("configuration_source_changed");
        }
        _source = bytes;
    }

    public JsonElement Read(ConfigurationSelector selector)
    {
        lock (_gate)
        {
            selector = ConfigurationPatchPolicy.Freeze(selector, allowRoot: true);
            ObjectDisposedException.ThrowIf(_source is null, this);
            if (_access.Read.IsEmpty) throw new ConfigurationException("configuration_access_denied");
            var root = Root();
            var selected = root.Resolve(selector);
            ConfigurationSpanAccess.Require(root, selector, selected, _access.Read, _access.Protected);
            return JsonSerializer.SerializeToElement(selected.Node.Value);
        }
    }

    public byte[] Apply(ConfigurationPatchRequest request)
    {
        lock (_gate)
        {
            request = ConfigurationPatchPolicy.Freeze(request);
            if (request.Binding != _binding) throw new ConfigurationException("configuration_patch_binding_conflict");
            ObjectDisposedException.ThrowIf(_source is null, this);
            if (_access.Write.IsEmpty) throw new ConfigurationException("configuration_access_denied");
            var root = Root();
            var edits = new List<BoundEdit>();
            var targets = new List<ResolvedConfigurationNode>();
            foreach (var edit in request.Edits)
            {
                var selected = edit.ExpectedExists ? root.Resolve(edit.Selector) : root.Missing(edit.Selector);
                ConfigurationSpanAccess.Require(root, edit.Selector, selected, _access.Write, _access.Protected);
                if (targets.Any(previous => ConfigurationSpanAccess.Prefix(previous.Path, selected.Path) ||
                    ConfigurationSpanAccess.Prefix(selected.Path, previous.Path)))
                    throw new ConfigurationException("configuration_patch_overlap");
                targets.Add(selected);
                if (edit.ExpectedExists && !JsonNode.DeepEquals(selected.Node.Value, Value(edit.Expected)))
                    throw new ConfigurationException("configuration_patch_expected_mismatch");
                if (edit.ExpectedExists && JsonNode.DeepEquals(selected.Node.Value, Value(edit.Value))) continue;
                byte[] replacement;
                if (edit.ExpectedExists) replacement = BoundedJson.Serialize(edit.Value);
                else
                {
                    var insertion = ConfigurationPropertyInsertion.Create(_source!, _syntax, selected, edit.Value,
                        edits.Count(previous => previous.Target.Path.Count == selected.Path.Count &&
                            previous.Target.Path.Take(selected.Path.Count - 1).SequenceEqual(selected.Path.Take(selected.Path.Count - 1))));
                    selected = new(new(insertion.Offset, insertion.Offset, null), selected.Path);
                    replacement = insertion.Bytes;
                }
                edits.Add(new(selected, edit.Value, replacement));
            }
            var expected = root.Value?.DeepClone();
            foreach (var edit in edits) Replace(expected, edit.Target.Path, Value(edit.Value));
            var result = Splice(edits);
            try
            {
                var parsed = Parse(result);
                if (!JsonNode.DeepEquals(parsed.Value, expected)) throw new ConfigurationException("configuration_patch_semantics_changed");
                VerifyIdentityPaths(root, parsed, _access.Protected.Concat(request.Edits.Where(edit => edit.ExpectedExists).Select(edit => edit.Selector)));
                foreach (var edit in request.Edits.Where(edit => !edit.ExpectedExists))
                    if (!root.Missing(edit.Selector).Path.SequenceEqual(parsed.Resolve(edit.Selector).Path))
                        throw new ConfigurationException("configuration_selector_identity_changed");
                return result;
            }
            catch { CryptographicOperations.ZeroMemory(result); throw; }
        }
    }

    private ConfigurationSpanNode Root()
    {
        ObjectDisposedException.ThrowIf(_source is null, this);
        if (!IsStructured) throw new ConfigurationException("configuration_resource_opaque");
        return _root ??= Parse(_source);
    }

    private ConfigurationSpanNode Parse(byte[] bytes)
    {
        try
        {
            var offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
            _ = new UTF8Encoding(false, true).GetCharCount(bytes.AsSpan(offset));
            return _syntax == ConfigurationSyntax.Yaml
                ? new YamlConfigurationSpans(new(bytes, offset)).Parse()
                : JsonConfigurationSpans.Parse(bytes, offset, _syntax == ConfigurationSyntax.JsonWithComments);
        }
        catch (Exception error) when (error is YamlException or JsonException or DecoderFallbackException or OverflowException or FormatException or InvalidOperationException)
        { throw new ConfigurationException("configuration_document_invalid"); }
    }

    private byte[] Splice(IReadOnlyList<BoundEdit> edits)
    {
        using var output = new MemoryStream();
        var previous = 0;
        foreach (var edit in edits.OrderBy(edit => edit.Target.Node.Start))
        {
            var node = edit.Target.Node;
            if (node.Start < previous || node.End < node.Start || node.End > _source!.Length)
                throw new ConfigurationException("configuration_span_invalid");
            Write(output, _source.AsSpan(previous, node.Start - previous));
            if (_syntax == ConfigurationSyntax.Yaml && node.Start == node.End && node.Start > 0 && _source[node.Start - 1] is (byte)':' or (byte)'-' or (byte)'?')
                Write(output, " "u8);
            Write(output, edit.Replacement);
            if (_syntax == ConfigurationSyntax.Yaml && node.End < _source.Length && _source[node.End] == '#') Write(output, " "u8);
            previous = node.End;
        }
        Write(output, _source!.AsSpan(previous));
        return output.ToArray();
    }

    private static void Write(Stream output, ReadOnlySpan<byte> bytes)
    {
        if (output.Length + bytes.Length > StrictJson.MaximumBytes) throw new ConfigurationException("configuration_document_limit");
        output.Write(bytes);
    }

    private static JsonNode? Value(JsonElement element)
    {
        var bytes = BoundedJson.Serialize(element);
        using var parsed = StrictJson.Parse(bytes);
        return JsonNode.Parse(bytes);
    }

    private static void Replace(JsonNode? root, IReadOnlyList<object> path, JsonNode? value)
    {
        var parent = root;
        for (var index = 0; index < path.Count - 1; index++)
            parent = path[index] is string name ? parent![name] : parent![(int)path[index]];
        if (path[^1] is string property) parent![property] = value;
        else parent![(int)path[^1]] = value;
    }

    private static void VerifyIdentityPaths(ConfigurationSpanNode before, ConfigurationSpanNode after, IEnumerable<ConfigurationSelector> selectors)
    {
        static IReadOnlyList<object>? Resolve(ConfigurationSpanNode root, ConfigurationSelector selector)
        {
            try { return root.Resolve(selector).Path; }
            catch (ConfigurationException error) when (error.Message == "configuration_selector_not_found") { return null; }
        }
        foreach (var selector in selectors)
        {
            var oldPath = Resolve(before, selector);
            var nextPath = Resolve(after, selector);
            if ((oldPath is null) != (nextPath is null) || oldPath is not null && !oldPath.SequenceEqual(nextPath!))
                throw new ConfigurationException("configuration_selector_identity_changed");
        }
    }

    public override string ToString() => nameof(ConfigurationDocument);
    public void Dispose()
    {
        lock (_gate)
        {
            if (_source is null) return;
            CryptographicOperations.ZeroMemory(_source); _source = null; _root = null;
        }
    }
    private sealed record BoundEdit(ResolvedConfigurationNode Target, JsonElement Value, byte[] Replacement);
}
