using System.Collections.ObjectModel;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Extensibility.Networking;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class InstalledHttpPort(InstalledExtensionConsumption consumption, ExtensionScope scope,
    ExtensionManifest manifest, Func<HttpClient> createClient) : IExtensionHttp
{
    private readonly SemaphoreSlim _transfers = new(4, 4);
    private readonly HashSet<string> _origins = new(manifest.Permissions.NetworkOrigins ?? [], StringComparer.Ordinal);

    public async ValueTask<ExtensionHttpResponse> SendAsync(ExtensionHttpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (address, method, headers, content, contentType, maximum) = Freeze(request);
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, scope.Cancellation);
            cancellation.CancelAfter(TimeSpan.FromSeconds(30));
            using var use = await consumption.BeginAsync(cancellation.Token).ConfigureAwait(false);
            if (!await _transfers.WaitAsync(0, cancellation.Token).ConfigureAwait(false))
                throw new IOException("extension_http_transfer_limit");
            try
            {
                use.RequireCurrent();
                using var client = createClient();
                using var message = new HttpRequestMessage(new HttpMethod(method), address);
                if (content.Length != 0 || contentType is not null)
                {
                    message.Content = new ByteArrayContent(content);
                    if (contentType is not null) message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
                }
                foreach (var (name, value) in headers)
                    if (!message.Headers.TryAddWithoutValidation(name, value)) throw new ArgumentException("extension_http_header_invalid");
                message.Headers.AcceptEncoding.Clear(); message.Headers.AcceptEncoding.Add(new("identity"));
                using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellation.Token).ConfigureAwait(false);
                use.RequireCurrent();
                if (response.RequestMessage?.RequestUri != address) throw new IOException("extension_http_redirect_unbound");
                if (response.Content.Headers.ContentEncoding.Any(value => value != "identity"))
                    throw new IOException("extension_http_content_encoding_invalid");
                if (response.Content.Headers.ContentLength > maximum) throw new IOException("extension_http_response_limit");
                var responseHeaders = ReadHeaders(response);
                await using var input = await response.Content.ReadAsStreamAsync(cancellation.Token).ConfigureAwait(false);
                var bytes = await ReadContentAsync(input, maximum, cancellation.Token).ConfigureAwait(false);
                try
                {
                    use.RequireCurrent(); cancellation.Token.ThrowIfCancellationRequested();
                    return new((int)response.StatusCode, responseHeaders, bytes);
                }
                catch { CryptographicOperations.ZeroMemory(bytes); throw; }
            }
            finally { _transfers.Release(); }
        }
        catch (HttpRequestException) { throw new IOException("extension_http_transport_failed"); }
        finally { CryptographicOperations.ZeroMemory(content); }
    }

    private (Uri Address, string Method, Dictionary<string, string> Headers, byte[] Content, string? ContentType, int Maximum)
        Freeze(ExtensionHttpRequest request)
    {
        var address = request.Address;
        if (address is null || !address.IsAbsoluteUri || address.Scheme is not ("https" or "http") ||
            address.UserInfo.Length != 0 || address.Fragment.Length != 0 || address.AbsoluteUri.Length > 8192 ||
            !_origins.Contains(address.GetLeftPart(UriPartial.Authority)))
            throw new UnauthorizedAccessException("extension_http_origin_not_declared");
        if (request.Method is not ("GET" or "POST" or "PUT" or "DELETE" or "PATCH" or "HEAD" or "OPTIONS") ||
            request.Content.Length > 1048576 || request.MaximumResponseBytes is < 1 or > 8388608 ||
            request.Headers is null || request.Headers.Count > 32 || request.ContentType?.Length > 128)
            throw new ArgumentException("extension_http_request_invalid");
        if (request.ContentType is not null && !MediaTypeHeaderValue.TryParse(request.ContentType, out _))
            throw new ArgumentException("extension_http_content_type_invalid");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in request.Headers)
        {
            if (name.Length is < 1 or > 80 || name.Any(character => !char.IsAsciiLetterOrDigit(character) && !"!#$%&'*+-.^_`|~".Contains(character)) ||
                value is null || value.Length > 8192 || value.Any(character => character is '\r' or '\n' or '\0') ||
                name.Equals("Host", StringComparison.OrdinalIgnoreCase) || name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) || name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase) ||
                !headers.TryAdd(name, value)) throw new ArgumentException("extension_http_header_invalid");
        }
        return (new Uri(address.AbsoluteUri), request.Method, headers, request.Content.ToArray(), request.ContentType, request.MaximumResponseBytes);
    }

    private static IReadOnlyDictionary<string, string[]> ReadHeaders(HttpResponseMessage response)
    {
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var length = 0;
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
        {
            var frozen = values.ToArray(); length += name.Length + frozen.Sum(value => value.Length);
            if (result.Count >= 64 || frozen.Length > 32 || length > 32768 || !result.TryAdd(name, frozen))
                throw new IOException("extension_http_response_headers_limit");
        }
        return new ReadOnlyDictionary<string, string[]>(result);
    }

    private static async Task<byte[]> ReadContentAsync(Stream input, int maximum, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[Math.Min(65536, maximum + 1)];
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximum + 1 - (int)output.Length)), cancellationToken).ConfigureAwait(false);
                if (read == 0) return output.ToArray();
                if (output.Length + read > maximum) throw new IOException("extension_http_response_limit");
                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (output.TryGetBuffer(out var retained)) CryptographicOperations.ZeroMemory(retained.AsSpan());
        }
    }
}
