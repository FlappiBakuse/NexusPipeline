using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusPipeline.Services.Execution;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>
/// 判断脚本 Python 调用截图的临时 loopback RPC。每次判断脚本 invocation 独立创建，
/// 仅接受当前随机令牌的 POST /capture 请求，调用方收尾时立即关闭监听。
/// </summary>
internal sealed class JudgeScreenshotBridge : IAsyncDisposable
{
    private const int MaxHeaderBytes = 8 * 1024;
    private const int MaxBodyBytes = 1024;

    private readonly TcpListener _listener;
    private readonly Func<CancellationToken, Task<RunScreenshot?>> _capture;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _acceptLoop;

    private JudgeScreenshotBridge(
        TcpListener listener,
        Func<CancellationToken, Task<RunScreenshot?>> capture,
        string endpoint,
        string token)
    {
        _listener = listener;
        _capture = capture;
        Endpoint = endpoint;
        Token = token;
        _acceptLoop = AcceptLoopAsync();
    }

    public string Endpoint { get; }

    public string Token { get; }

    public static JudgeScreenshotBridge Start(Func<CancellationToken, Task<RunScreenshot?>> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        return new JudgeScreenshotBridge(listener, capture, $"http://127.0.0.1:{port}/capture", token);
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
            Logger.Warn($"[判断脚本] Python 截图回环桥接异常：{ex.Message}");
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
            if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(path, "/capture", StringComparison.Ordinal))
            {
                await WriteResponseAsync(stream, 404, "截图接口不存在", token).ConfigureAwait(false);
                return;
            }
            if (!headers.TryGetValue("x-nexus-screenshot-token", out string? requestToken)
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(requestToken),
                    Encoding.UTF8.GetBytes(Token)))
            {
                await WriteResponseAsync(stream, 401, "截图接口令牌无效", token).ConfigureAwait(false);
                return;
            }
            if (headers.TryGetValue("content-length", out string? contentLengthText)
                && (!int.TryParse(contentLengthText, out int contentLength) || contentLength > MaxBodyBytes))
            {
                await WriteResponseAsync(stream, 413, "截图接口请求体过大", token).ConfigureAwait(false);
                return;
            }
            if (headers.TryGetValue("content-length", out contentLengthText)
                && int.TryParse(contentLengthText, out int bodyLength)
                && bodyLength > 0)
            {
                await DrainAsync(stream, bodyLength, token).ConfigureAwait(false);
            }

            RunScreenshot? screenshot = await _capture(token).ConfigureAwait(false);
            if (screenshot is null)
            {
                await WriteResponseAsync(stream, 500, "截图采集失败", token).ConfigureAwait(false);
                return;
            }
            await WriteResponseAsync(stream, 200, null, token, screenshot.Id).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.Warn($"[判断脚本] Python 截图请求处理失败：{ex.Message}");
            try
            {
                await WriteResponseAsync(stream, 500, "截图接口处理失败", CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

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
        return null;
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

    private static async Task DrainAsync(NetworkStream stream, int length, CancellationToken token)
    {
        byte[] buffer = new byte[Math.Min(256, length)];
        int remaining = length;
        while (remaining > 0)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("截图接口请求体提前结束");
            }
            remaining -= read;
        }
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int statusCode,
        string? error,
        CancellationToken token,
        string? id = null)
    {
        object payload = error is null
            ? new { ok = true, id }
            : new { ok = false, error };
        byte[] body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        string status = statusCode switch
        {
            200 => "OK",
            401 => "Unauthorized",
            404 => "Not Found",
            413 => "Payload Too Large",
            _ => "Internal Server Error",
        };
        string header = $"HTTP/1.1 {statusCode} {status}\r\n"
            + "Content-Type: application/json; charset=utf-8\r\n"
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
