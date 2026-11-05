using Json.Schema;
using System.Text.Json;

namespace NexusPipeline.Platform.Serialization;

internal sealed class AbsoluteUriFormat() : Format("uri")
{
    public override bool Validate(JsonElement value, out string? errorMessage)
    {
        errorMessage = "invalid_absolute_uri";
        if (value.ValueKind != JsonValueKind.String)
            return true;
        var text = value.GetString()!;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] <= ' ' || text[index] >= '\u007f')
                return false;
            if (text[index] == '%' &&
                (index + 2 >= text.Length || !Uri.IsHexDigit(text[index + 1]) || !Uri.IsHexDigit(text[index + 2])))
                return false;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.Host))
            return false;
        errorMessage = null;
        return true;
    }
}
