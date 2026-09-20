using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Modules.History.Contracts;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.Modules.Execution.Judgement;

/// <summary>
/// 判断脚本 invocation 的临时 loopback RPC。它同时承载截图与只读探针，
/// 每次 invocation 使用独立随机令牌，收尾时立即关闭监听。
/// </summary>
internal sealed class JudgeRuntimeBridge : IAsyncDisposable
{
    internal const int MaxHeaderBytes = 8 * 1024;
    internal const int MaxBodyBytes = 8 * 1024;
    internal const int MaxResponseBytes = 2 * 1024 * 1024;

    private readonly TcpListener _listener;
    private readonly Func<CancellationToken, Task<RunScreenshot?>> _capture;
    private readonly OutboundHttpClientProvider? _http;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _acceptLoop;

    private JudgeRuntimeBridge(
        TcpListener listener,
        Func<CancellationToken, Task<RunScreenshot?>> capture,
        string endpoint,
        string token,
        OutboundHttpClientProvider? http)
    {
        _listener = listener;
        _capture = capture;
        Endpoint = endpoint;
        ScreenshotEndpoint = endpoint + "/capture";
        Token = token;
        _http = http;
        _acceptLoop = AcceptLoopAsync();
    }

    /// <summary>桥接根地址；probeApi.endpoint 使用此地址。</summary>
    internal string Endpoint { get; }

    /// <summary>兼容已有 Python 截图脚本的 /capture 地址。</summary>
    internal string ScreenshotEndpoint { get; }

    internal string Token { get; }

    internal static JudgeRuntimeBridge Start(
        Func<CancellationToken, Task<RunScreenshot?>>? capture = null,
        OutboundHttpClientProvider? http = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        return new JudgeRuntimeBridge(
            listener,
            capture ?? (_ => Task.FromResult<RunScreenshot?>(null)),
            $"http://127.0.0.1:{port}",
            token,
            http);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(_lifetime.Token).ConfigureAwait(false);
                await HandleClientAsync(client).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (SocketException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.Warn($"[判断脚本] 回环运行时桥接异常：{ex.Message}");
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using NetworkStream stream = client.GetStream();
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        requestCts.CancelAfter(TimeSpan.FromSeconds(30));
        CancellationToken token = requestCts.Token;

        try
        {
            byte[]? headerBytes = await ReadHeadersAsync(stream, token).ConfigureAwait(false);
            if (headerBytes is null)
            {
                return;
            }
            (string method, string path, Dictionary<string, string> headers) = ParseHeaders(headerBytes);
            if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await WriteErrorAsync(stream, 404, "运行时接口不存在", token).ConfigureAwait(false);
                return;
            }
            if (!IsKnownPath(path))
            {
                await WriteErrorAsync(stream, 404, "运行时接口不存在", token).ConfigureAwait(false);
                return;
            }
            if (!headers.TryGetValue("content-length", out string? contentLengthText))
            {
                contentLengthText = "0";
            }
            if (!int.TryParse(contentLengthText, out int contentLength)
                || contentLength < 0
                || contentLength > MaxBodyBytes)
            {
                await WriteErrorAsync(stream, 413, "运行时接口请求体过大", token).ConfigureAwait(false);
                return;
            }
            byte[] body = contentLength == 0
                ? Array.Empty<byte>()
                : await ReadBodyAsync(stream, contentLength, token).ConfigureAwait(false);

            // 先消费受限请求体，再返回鉴权结果，避免调用方已发送 body 时被提前关闭连接。
            if (!HasValidToken(path, headers))
            {
                await WriteErrorAsync(stream, 401, "运行时接口令牌无效", token).ConfigureAwait(false);
                return;
            }

            switch (path)
            {
                case "/capture":
                    await HandleCaptureAsync(stream, token).ConfigureAwait(false);
                    break;
                case "/processes":
                    await WriteJsonAsync(stream, 200, JudgeProbeService.ListProcessesJson(DecodeBody(body)), token).ConfigureAwait(false);
                    break;
                case "/windows":
                    await WriteJsonAsync(stream, 200, JudgeProbeService.ListWindowsJson(DecodeBody(body)), token).ConfigureAwait(false);
                    break;
                case "/http-probe":
                    await HandleHttpProbeAsync(stream, body, token).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || token.IsCancellationRequested)
        {
        }
        catch (RequestHeadersTooLargeException)
        {
            try
            {
                await WriteErrorAsync(stream, 431, "运行时接口请求头过大", CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }
        catch (JsonException)
        {
            try
            {
                await WriteErrorAsync(stream, 400, "运行时接口请求格式无效", CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[判断脚本] 回环运行时请求处理失败：{ex.Message}");
            try
            {
                await WriteErrorAsync(stream, 500, "运行时接口处理失败", CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private async Task HandleCaptureAsync(NetworkStream stream, CancellationToken token)
    {
        RunScreenshot? screenshot = await _capture(token).ConfigureAwait(false);
        if (screenshot is null)
        {
            await WriteErrorAsync(stream, 500, "截图采集失败", token).ConfigureAwait(false);
            return;
        }
        await WriteJsonAsync(
            stream,
            200,
            JsonSerializer.Serialize(new { ok = true, id = screenshot.Id }),
            token).ConfigureAwait(false);
    }

    private async Task HandleHttpProbeAsync(NetworkStream stream, byte[] body, CancellationToken token)
    {
        string requestText = DecodeBody(body);
        using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(requestText) ? "{}" : requestText);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("http-probe request must be an object");
        }
        string url = document.RootElement.TryGetProperty("url", out JsonElement urlElement)
            ? urlElement.GetString() ?? ""
            : "";
        string options = document.RootElement.TryGetProperty("options", out JsonElement optionsElement)
            ? optionsElement.GetRawText()
            : "{}";
        string result = await JudgeHttpProbe.RunAsync(url, options, _http, token).ConfigureAwait(false);
        await WriteJsonAsync(stream, 200, result, token).ConfigureAwait(false);
    }

    private bool HasValidToken(string path, IReadOnlyDictionary<string, string> headers)
    {
        string headerName = path == "/capture"
            ? headers.ContainsKey("x-nexus-judge-token") ? "x-nexus-judge-token" : "x-nexus-screenshot-token"
            : "x-nexus-judge-token";
        return headers.TryGetValue(headerName, out string? requestToken)
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(requestToken),
                Encoding.UTF8.GetBytes(Token));
    }

    private static bool IsKnownPath(string path) =>
        path is "/capture" or "/processes" or "/windows" or "/http-probe";

    private static async Task<byte[]?> ReadHeadersAsync(NetworkStream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        byte[] one = new byte[1];
        while (buffer.Length < MaxHeaderBytes)
        {
            int read = await stream.ReadAsync(one.AsMemory(), token).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }
            buffer.WriteByte(one[0]);
            if (buffer.Length >= 4)
            {
                byte[] bytes = buffer.GetBuffer();
                int length = checked((int)buffer.Length);
                if (bytes[length - 4] == '\r'
                    && bytes[length - 3] == '\n'
                    && bytes[length - 2] == '\r'
                    && bytes[length - 1] == '\n')
                {
                    return buffer.ToArray();
                }
            }
        }
        throw new RequestHeadersTooLargeException();
    }

    private sealed class RequestHeadersTooLargeException : Exception
    {
    }

    private static (string Method, string Path, Dictionary<string, string> Headers) ParseHeaders(byte[] bytes)
    {
        string text = Encoding.ASCII.GetString(bytes);
        string[] lines = text.Split("\r\n", StringSplitOptions.None);
        string[] request = lines.FirstOrDefault()?.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }
            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return (
            request.Length > 0 ? request[0] : "",
            request.Length > 1 ? request[1] : "",
            headers);
    }

    private static async Task<byte[]> ReadBodyAsync(NetworkStream stream, int length, CancellationToken token)
    {
        byte[] body = new byte[length];
        int offset = 0;
        while (offset < body.Length)
        {
            int read = await stream.ReadAsync(body.AsMemory(offset), token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("运行时接口请求体提前结束");
            }
            offset += read;
        }
        return body;
    }

    private static string DecodeBody(byte[] body) => Encoding.UTF8.GetString(body);

    private static async Task WriteJsonAsync(NetworkStream stream, int statusCode, string json, CancellationToken token)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        if (body.Length > MaxResponseBytes)
        {
            await WriteErrorAsync(stream, 413, "运行时接口响应过大", token).ConfigureAwait(false);
            return;
        }
        await WriteResponseAsync(stream, statusCode, body, "application/json; charset=utf-8", token).ConfigureAwait(false);
    }

    private static Task WriteErrorAsync(NetworkStream stream, int statusCode, string error, CancellationToken token) =>
        WriteJsonAsync(stream, statusCode, JsonSerializer.Serialize(new { ok = false, error }), token);

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int statusCode,
        byte[] body,
        string contentType,
        CancellationToken token)
    {
        string status = statusCode switch
        {
            200 => "OK",
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            413 => "Payload Too Large",
            431 => "Request Header Fields Too Large",
            _ => "Internal Server Error",
        };
        string header = $"HTTP/1.1 {statusCode} {status}\r\n"
            + $"Content-Type: {contentType}\r\n"
            + $"Content-Length: {body.Length}\r\n"
            + "Connection: close\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes.AsMemory(), token).ConfigureAwait(false);
        await stream.WriteAsync(body.AsMemory(), token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        _lifetime.Dispose();
    }
}
