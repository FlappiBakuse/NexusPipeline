using System.Buffers;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Host.Control.Http;

internal static class ControlRequestBody
{
    internal const int MaximumBytes = StrictJson.MaximumBytes;
    internal static async Task<ControlJsonBody> ReadAsync(HttpContext context, int maximum = MaximumBytes)
    {
        if (maximum is < 1 or > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(maximum));
        if (context.Request.Headers.ContentEncoding.Count != 0) throw new ControlRequestException(415, "request_encoding_unsupported");
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var content) ||
            !string.Equals(content.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
            content.Parameters.Count > 1 || content.Parameters.Any(value => !value.Name.Equals("charset", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(value.Value?.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase)))
            throw new ControlRequestException(415, "request_media_type_invalid");
        if (context.Request.ContentLength > maximum) throw new ControlRequestException(413, "request_too_large");
        var initial = context.Request.ContentLength is { } length && length >= 0 ? (int)length + 1 : 8192;
        var bytes = ArrayPool<byte>.Shared.Rent(Math.Min(maximum + 1, initial));
        var count = 0;
        try
        {
            while (count <= maximum)
            {
                if (count == bytes.Length)
                {
                    var expanded = ArrayPool<byte>.Shared.Rent(Math.Min(maximum + 1, bytes.Length * 2));
                    bytes.AsSpan(0, count).CopyTo(expanded);
                    CryptographicOperations.ZeroMemory(bytes);
                    ArrayPool<byte>.Shared.Return(bytes);
                    bytes = expanded;
                }
                var read = await context.Request.Body.ReadAsync(bytes.AsMemory(count,
                    Math.Min(bytes.Length - count, maximum + 1 - count)), context.RequestAborted);
                if (read == 0) break;
                count += read;
            }
            if (count > maximum) throw new ControlRequestException(413, "request_too_large");
            var owned = bytes.AsSpan(0, count).ToArray();
            try { return new(StrictJson.Parse(owned), owned); }
            catch { CryptographicOperations.ZeroMemory(owned); throw; }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    internal static void RequireMembers(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            value.EnumerateObject().Any(member => !names.Contains(member.Name, StringComparer.Ordinal)))
            throw new ControlRequestException(400, "request_shape_invalid");
    }

    internal static Guid Uuid(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && Guid.TryParseExact(value.GetString(), "D", out var id) &&
        id != Guid.Empty && value.GetString() == id.ToString("D") ? id :
        throw new ControlRequestException(400, "request_identity_invalid");
}

internal sealed class ControlJsonBody(JsonDocument document, byte[] owned) : IDisposable
{
    internal JsonElement Root => document.RootElement;
    internal void RequireSchema(ContractReader contracts, string schemaName)
        => contracts.RequireSchema(document, owned, schemaName);
    public void Dispose()
    {
        document.Dispose();
        CryptographicOperations.ZeroMemory(owned);
    }
}

internal sealed class ControlRequestException(int status, string code) : Exception(code)
{
    internal int Status { get; } = status;
}
