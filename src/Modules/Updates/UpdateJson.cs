using System.Text.Json;
using System.Text.Json.Nodes;

namespace NexusPipeline.Modules.Updates;

internal static class UpdateJson
{
    internal static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken token)
    {
        if (content.Headers.ContentLength is long declared && (declared < 1 || declared > limit))
            throw new InvalidDataException("update.metadata_size");
        using var buffer = new MemoryStream();
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        byte[] block = new byte[8192];
        while (true)
        {
            int read = await stream.ReadAsync(block, token).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > limit) throw new InvalidDataException("update.metadata_size");
            buffer.Write(block, 0, read);
        }
        return buffer.ToArray();
    }

    internal static JsonNode Parse(ReadOnlyMemory<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        int nodes = 0;
        void Visit(JsonElement value)
        {
            if (++nodes > 16384) throw new InvalidDataException("update.metadata_nodes");
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new InvalidDataException("update.metadata_duplicate_field");
                    Visit(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray()) Visit(item);
        }
        Visit(document.RootElement);
        return JsonNode.Parse(bytes.Span)!;
    }
}
