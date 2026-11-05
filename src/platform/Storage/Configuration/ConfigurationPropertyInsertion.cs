using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Configuration;

internal static class ConfigurationPropertyInsertion
{
    internal static (int Offset, byte[] Bytes) Create(byte[] source, ConfigurationSyntax syntax,
        ResolvedConfigurationNode missing, JsonElement value, int preceding)
    {
        var parent = missing.Node;
        var name = JsonSerializer.Serialize((string)missing.Path[^1]);
        var content = Encoding.UTF8.GetString(BoundedJson.Serialize(value));
        if (syntax != ConfigurationSyntax.Yaml || source[parent.Start] == '{')
        {
            var offset = parent.Properties!.Count == 0 ? parent.Start + 1 : parent.Properties.Values.Max(node => node.End);
            var comma = parent.Properties.Count > 0 || preceding > 0 ? "," : "";
            return (offset, Encoding.UTF8.GetBytes(comma + name + ":" + content));
        }
        var lineStart = parent.Start;
        while (lineStart > 0 && source[lineStart - 1] != '\n') lineStart--;
        var indent = parent.Start - lineStart;
        if (lineStart == 0 && source.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) indent -= 3;
        var lineEnd = parent.End;
        while (lineEnd < source.Length && source[lineEnd] != '\n') lineEnd++;
        var newline = source.AsSpan().Contains((byte)'\r') ? "\r\n" : "\n";
        var prefix = lineEnd == source.Length ? newline : "";
        var insertion = prefix + new string(' ', indent) + name + ": " + content + newline;
        return (lineEnd < source.Length ? lineEnd + 1 : lineEnd, Encoding.UTF8.GetBytes(insertion));
    }
}
