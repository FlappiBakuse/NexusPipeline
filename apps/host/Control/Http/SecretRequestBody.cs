using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Core.Secrets;

namespace NexusPipeline.Host.Control.Http;

internal sealed class SecretRequestBody : IDisposable
{
    public const int MaximumBytes = 196608;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly byte[] _bytes;
    public int Length { get; }
    private SecretRequestBody(byte[] bytes, int length) { _bytes = bytes; Length = length; }

    public static async Task<SecretRequestBody> ReadAsync(HttpContext context, int maximum)
    {
        if (context.Request.ContentLength > maximum) throw new SecretBodyException(413, "request_too_large");
        var bytes = new byte[maximum + 1];
        try
        {
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await context.Request.Body.ReadAsync(bytes.AsMemory(length), context.RequestAborted);
                if (read == 0) return new(bytes, length);
                length += read;
            }
            throw new SecretBodyException(413, "request_too_large");
        }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }

    public SecretValue Decode()
    {
        char[]? characters = null;
        try
        {
            var count = Utf8.GetCharCount(_bytes.AsSpan(0, Length));
            if (count is < 1 or > 65536) throw new SecretBodyException(400, "secret_value_invalid");
            characters = new char[count];
            Utf8.GetChars(_bytes.AsSpan(0, Length), characters);
            return new(characters);
        }
        catch (DecoderFallbackException) { throw new SecretBodyException(400, "secret_value_invalid"); }
        finally { if (characters is not null) Array.Clear(characters); }
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_bytes);
}

internal sealed class SecretBodyException(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
}
