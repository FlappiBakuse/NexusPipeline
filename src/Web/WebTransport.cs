using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NexusPipeline.Web;

/// <summary>
/// Web API 使用的最小请求上下文。生产宿主由 HttpListener 适配，普通权限 Test Host
/// 使用托管 loopback transport，业务 handler 和路由解析共用这一层。
/// </summary>
internal sealed class WebContext
{
    public WebContext(WebRequest request, WebResponse response)
    {
        Request = request;
        Response = response;
    }

    public WebRequest Request { get; }

    public WebResponse Response { get; }

    public static WebContext FromHttpListener(System.Net.HttpListenerContext context)
    {
        return new WebContext(
            WebRequest.FromHttpListener(context.Request),
            WebResponse.FromHttpListener(context.Response));
    }
}

internal sealed class WebRequest
{
    public WebRequest(
        Uri? url,
        string httpMethod,
        NameValueCollection queryString,
        NameValueCollection headers,
        Stream inputStream,
        long contentLength64,
        string? contentType,
        Encoding? contentEncoding,
        bool hasEntityBody,
        IPEndPoint? remoteEndPoint)
    {
        Url = url;
        HttpMethod = httpMethod;
        QueryString = queryString;
        Headers = headers;
        InputStream = inputStream;
        ContentLength64 = contentLength64;
        ContentType = contentType;
        ContentEncoding = contentEncoding;
        HasEntityBody = hasEntityBody;
        RemoteEndPoint = remoteEndPoint;
    }

    public Uri? Url { get; }

    public string HttpMethod { get; }

    public NameValueCollection QueryString { get; }

    public NameValueCollection Headers { get; }

    public Stream InputStream { get; }

    public long ContentLength64 { get; }

    public string? ContentType { get; }

    public Encoding? ContentEncoding { get; }

    public bool HasEntityBody { get; }

    public IPEndPoint? RemoteEndPoint { get; }

    internal static WebRequest FromHttpListener(System.Net.HttpListenerRequest request)
    {
        return new WebRequest(
            request.Url,
            request.HttpMethod,
            CopyCollection(request.QueryString),
            CopyCollection(request.Headers),
            request.InputStream,
            request.ContentLength64,
            request.ContentType,
            request.ContentEncoding,
            request.HasEntityBody,
            request.RemoteEndPoint);
    }

    internal static NameValueCollection CopyCollection(NameValueCollection source)
    {
        var copy = new NameValueCollection(StringComparer.OrdinalIgnoreCase);
        foreach (string? key in source.AllKeys)
        {
            if (key is not null)
            {
                copy[key] = source[key];
            }
        }
        return copy;
    }
}

internal sealed class WebHeaderMap : IEnumerable<KeyValuePair<string, string>>
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public string? this[string name]
    {
        get => _values.TryGetValue(name, out string? value) ? value : null;
        set
        {
            if (value is null)
            {
                _values.Remove(name);
            }
            else
            {
                _values[name] = value;
            }
        }
    }

    public bool Contains(string name) => _values.ContainsKey(name);

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _values.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class WebResponse
{
    public WebResponse(Stream outputStream)
    {
        OutputStream = outputStream;
    }

    public int StatusCode { get; set; } = 200;

    public string? ContentType { get; set; }

    public WebHeaderMap Headers { get; } = new();

    public long ContentLength64 { get; set; } = -1;

    public Stream OutputStream { get; }

    public void Close() => OutputStream.Close();

    internal static WebResponse FromHttpListener(System.Net.HttpListenerResponse response)
    {
        WebResponse? result = null;
        var output = new ResponseStream(
            response.OutputStream,
            () => ApplyHttpListenerResponse(result!, response));
        result = new WebResponse(output);
        return result;
    }

    internal static WebResponse ForManagedStream(Stream stream)
    {
        WebResponse? result = null;
        var output = new ResponseStream(
            stream,
            () => ManagedHttpTransport.WriteResponseHeaders(stream, result!));
        result = new WebResponse(output);
        return result;
    }

    private static void ApplyHttpListenerResponse(WebResponse source, System.Net.HttpListenerResponse target)
    {
        target.StatusCode = source.StatusCode;
        if (source.ContentType is not null)
        {
            target.ContentType = source.ContentType;
        }
        if (source.ContentLength64 >= 0)
        {
            target.ContentLength64 = source.ContentLength64;
        }
        foreach ((string key, string value) in source.Headers)
        {
            target.Headers[key] = value;
        }
    }
}

internal sealed class ResponseStream : Stream
{
    private readonly Stream _inner;
    private readonly Action _ensureHeaders;
    private bool _headersSent;
    private bool _disposed;

    public ResponseStream(Stream inner, Action ensureHeaders)
    {
        _inner = inner;
        _ensureHeaders = ensureHeaders;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => !_disposed && _inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        EnsureHeaders();
        _inner.Write(buffer, offset, count);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        EnsureHeaders();
        return _inner.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureHeaders();
        return _inner.WriteAsync(buffer, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try
            {
                EnsureHeaders();
            }
            finally
            {
                _inner.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    private void EnsureHeaders()
    {
        if (_headersSent) return;
        _headersSent = true;
        _ensureHeaders();
    }
}

internal static class ManagedHttpTransport
{
    private const int MaxHeaderBytes = 64 * 1024;
    private const long MaxBodyBytes = 32 * 1024 * 1024;

    public static async Task<WebRequest?> ReadRequestAsync(
        Stream stream,
        int port,
        IPEndPoint? remoteEndPoint,
        CancellationToken cancellationToken)
    {
        byte[] headerBytes = await ReadHeadersAsync(stream, cancellationToken).ConfigureAwait(false);
        if (headerBytes.Length == 0) return null;
        string headerText = Encoding.ASCII.GetString(headerBytes);
        string[] lines = headerText.Split("\r\n", StringSplitOptions.None);
        string[] requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2)
        {
            throw new InvalidDataException("HTTP 请求行无效");
        }

        var headers = new NameValueCollection(StringComparer.OrdinalIgnoreCase);
        for (int index = 1; index < lines.Length; index++)
        {
            string line = lines[index];
            if (line.Length == 0) break;
            int separator = line.IndexOf(':');
            if (separator <= 0) throw new InvalidDataException("HTTP 请求头无效");
            headers.Add(line[..separator].Trim(), line[(separator + 1)..].Trim());
        }

        string target = requestLine[1];
        string host = headers["Host"] ?? $"127.0.0.1:{port}";
        Uri requestUrl = CreateRequestUri(target, host);
        bool chunked = headers["Transfer-Encoding"]?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true;
        long contentLength = 0;
        if (!chunked && headers["Content-Length"] is string contentLengthText
            && (!long.TryParse(contentLengthText, out contentLength) || contentLength < 0))
        {
            throw new InvalidDataException("HTTP Content-Length 无效");
        }
        if (contentLength > MaxBodyBytes)
        {
            throw new InvalidDataException("HTTP 请求体过大");
        }

        byte[] body = chunked
            ? await ReadChunkedBodyAsync(stream, cancellationToken).ConfigureAwait(false)
            : await ReadFixedBodyAsync(stream, contentLength, cancellationToken).ConfigureAwait(false);
        string? contentType = headers["Content-Type"];
        return new WebRequest(
            requestUrl,
            requestLine[0],
            ParseQuery(requestUrl),
            headers,
            new MemoryStream(body, writable: false),
            body.LongLength,
            contentType,
            Encoding.UTF8,
            body.Length > 0,
            remoteEndPoint);
    }

    public static void WriteResponseHeaders(Stream stream, WebResponse response)
    {
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ")
            .Append(response.StatusCode)
            .Append(' ')
            .Append(GetReasonPhrase(response.StatusCode))
            .Append("\r\n");
        if (response.ContentType is not null && !response.Headers.Contains("Content-Type"))
        {
            builder.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
        }
        if (response.ContentLength64 >= 0 && !response.Headers.Contains("Content-Length"))
        {
            builder.Append("Content-Length: ").Append(response.ContentLength64).Append("\r\n");
        }
        foreach ((string key, string value) in response.Headers)
        {
            builder.Append(key).Append(": ").Append(value).Append("\r\n");
        }
        if (!response.Headers.Contains("Connection"))
        {
            builder.Append("Connection: close\r\n");
        }
        builder.Append("\r\n");
        byte[] bytes = Encoding.ASCII.GetBytes(builder.ToString());
        stream.Write(bytes, 0, bytes.Length);
    }

    private static async Task<byte[]> ReadHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        int match = 0;
        byte[] buffer = new byte[1];
        while (bytes.Count <= MaxHeaderBytes)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return bytes.Count == 0 ? Array.Empty<byte>() : throw new InvalidDataException("HTTP 请求头提前结束");
            }
            byte value = buffer[0];
            bytes.Add(value);
            match = value switch
            {
                (byte)'\r' when match is 0 or 2 => match + 1,
                (byte)'\n' when match is 1 or 3 => match + 1,
                _ => value == (byte)'\r' ? 1 : 0,
            };
            if (match == 4)
            {
                bytes.RemoveRange(bytes.Count - 4, 4);
                return bytes.ToArray();
            }
        }
        throw new InvalidDataException("HTTP 请求头过大");
    }

    private static async Task<byte[]> ReadFixedBodyAsync(Stream stream, long length, CancellationToken cancellationToken)
    {
        if (length == 0) return Array.Empty<byte>();
        byte[] body = new byte[(int)length];
        int offset = 0;
        while (offset < body.Length)
        {
            int read = await stream.ReadAsync(body.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new InvalidDataException("HTTP 请求体提前结束");
            offset += read;
        }
        return body;
    }

    private static async Task<byte[]> ReadChunkedBodyAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        while (true)
        {
            string line = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
            string sizeText = line.Split(';', 2)[0].Trim();
            if (!long.TryParse(sizeText, System.Globalization.NumberStyles.HexNumber, null, out long size) || size < 0)
            {
                throw new InvalidDataException("HTTP chunk 大小无效");
            }
            if (size == 0)
            {
                do
                {
                    line = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
                } while (line.Length > 0);
                break;
            }
            if (body.Length + size > MaxBodyBytes) throw new InvalidDataException("HTTP 请求体过大");
            byte[] chunk = await ReadFixedBodyAsync(stream, size, cancellationToken).ConfigureAwait(false);
            body.Write(chunk, 0, chunk.Length);
            string separator = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
            if (separator.Length != 0) throw new InvalidDataException("HTTP chunk 结尾无效");
        }
        return body.ToArray();
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        byte[] buffer = new byte[1];
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new InvalidDataException("HTTP 行提前结束");
            if (buffer[0] == (byte)'\r')
            {
                int next = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
                if (next == 0 || buffer[0] != (byte)'\n') throw new InvalidDataException("HTTP 行结尾无效");
                return Encoding.ASCII.GetString(bytes.ToArray());
            }
            bytes.Add(buffer[0]);
            if (bytes.Count > MaxHeaderBytes) throw new InvalidDataException("HTTP 行过长");
        }
    }

    private static Uri CreateRequestUri(string target, string host)
    {
        if (Uri.TryCreate(target, UriKind.Absolute, out Uri? absolute)) return absolute;
        string relative = target.StartsWith("/", StringComparison.Ordinal) ? target : "/" + target;
        return new Uri($"http://{host}{relative}", UriKind.Absolute);
    }

    private static NameValueCollection ParseQuery(Uri uri)
    {
        var query = new NameValueCollection(StringComparer.OrdinalIgnoreCase);
        string raw = uri.Query.TrimStart('?');
        foreach (string pair in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.IndexOf('=');
            string key = separator < 0 ? pair : pair[..separator];
            string value = separator < 0 ? "" : pair[(separator + 1)..];
            query.Add(Decode(key), Decode(value));
        }
        return query;
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    private static string GetReasonPhrase(int statusCode) => statusCode switch
    {
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        413 => "Payload Too Large",
        422 => "Unprocessable Entity",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        _ => "Response",
    };
}
