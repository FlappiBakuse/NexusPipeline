using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace NexusPipeline.Platform.Serialization;

public static class StrictJson
{
    private static readonly JsonSerializerOptions SourceJson = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
    public const int MaximumBytes = 2 * 1024 * 1024;
    public const int MaximumDepth = 64;
    public const int MaximumStringBytes = 256 * 1024;

    public static JsonDocument Parse(ReadOnlyMemory<byte> bytes)
        => Parse(bytes, MaximumBytes, MaximumStringBytes);

    internal static JsonDocument Parse(ReadOnlyMemory<byte> bytes, int maximumBytes, int maximumStringBytes)
    {
        if (maximumBytes is < 1 or > 32 * 1024 * 1024 || maximumStringBytes < 1 || maximumStringBytes > maximumBytes ||
            bytes.Length == 0 || bytes.Length > maximumBytes)
            throw new JsonException("invalid_json_size");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumDepth
            });
        }
        catch (JsonException)
        {
            throw new JsonException("invalid_json");
        }
        try
        {
            Inspect(document.RootElement, maximumStringBytes);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    internal static string ReadSource(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is < 1 or > 8 * 1024 * 1024) throw new JsonException("invalid_source_json_size");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = MaximumDepth });
        Inspect(document.RootElement, MaximumStringBytes);
        return document.RootElement.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Serialize(document.RootElement, SourceJson) : throw new JsonException("invalid_source_json_object");
    }

    private static void Inspect(JsonElement element, int maximumStringBytes)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new JsonException("duplicate_json_member");
                    CheckString(property.Name, MaximumStringBytes);
                    Inspect(property.Value, maximumStringBytes);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Inspect(item, maximumStringBytes);
                break;
            case JsonValueKind.Number:
                if (!element.TryGetDouble(out var number) || !double.IsFinite(number))
                    throw new JsonException("non_finite_json_number");
                break;
            case JsonValueKind.String:
                CheckString(element.GetString()!, maximumStringBytes);
                break;
        }
    }

    private static void CheckString(string value, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) > maximumBytes)
            throw new JsonException("json_string_limit");
    }
}
