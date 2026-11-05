using System.Text.Json;

namespace NexusPipeline.Host.Control.Mcp;

internal sealed record McpToolAnnotations(bool ReadOnlyHint, bool DestructiveHint, bool IdempotentHint, bool OpenWorldHint);
internal sealed record McpToolDescription(string Name, string Title, string Description, JsonElement InputSchema,
    McpToolAnnotations Annotations);
