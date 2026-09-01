using System.Net;
using System.Net.Sockets;
using System.Text;
using NexusPipeline.Models;
using NexusPipeline.Services;
using NexusPipeline.Services.Networking;
using NexusPipeline.Services.Notification;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class NotificationImageTests
{
    [Fact]
    public async Task DiscordWebhookSendsMultipartImage()
    {
        await using var server = new CapturingHttpServer(1, "", 204);
        var settings = new AppSettings
        {
            WebhookUrl = server.Url,
            WebhookType = "discord",
            WebhookTimeout = 5,
        };
        var image = new NotificationImage(
            "screenshot-1",
            "nexuspipeline-screenshot-1.jpg",
            "image/jpeg",
            new byte[] { 0x01, 0x02, 0x03 },
            640,
            480,
            DateTimeOffset.Now);

        bool ok = await WebhookSender.SendAsync(
            settings,
            "通知正文",
            new OutboundHttpClientProvider(() => settings),
            image);
        byte[] request = await server.Completion;
        string requestText = Encoding.Latin1.GetString(request);

        Assert.True(ok);
        Assert.Contains("multipart/form-data", requestText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("payload_json", requestText, StringComparison.Ordinal);
        Assert.Contains(image.FileName, requestText, StringComparison.Ordinal);
        Assert.Contains("通知正文", Encoding.UTF8.GetString(request));
        Assert.Contains("\u0001\u0002\u0003", requestText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WecomWebhookSendsTextThenImageMessage()
    {
        await using var server = new CapturingHttpServer(2, "{\"errcode\":0}", 200);
        var settings = new AppSettings
        {
            WebhookUrl = server.Url,
            WebhookType = "wecom",
            WebhookTimeout = 5,
        };
        var image = new NotificationImage(
            "screenshot-2",
            "nexuspipeline-screenshot-2.jpg",
            "image/jpeg",
            new byte[] { 0x10, 0x20, 0x30 },
            320,
            240,
            DateTimeOffset.Now);

        bool ok = await WebhookSender.SendAsync(
            settings,
            "通知正文",
            new OutboundHttpClientProvider(() => settings),
            image);
        byte[] request = await server.Completion;

        Assert.True(ok);
        string allRequests = Encoding.UTF8.GetString(request);
        Assert.Contains("\"msgtype\":\"text", allRequests, StringComparison.Ordinal);
        Assert.Contains("\"msgtype\":\"image", allRequests, StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(image.Data), allRequests, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenericWebhookExpandsImagePlaceholders()
    {
        await using var server = new CapturingHttpServer(1, "", 204);
        var settings = new AppSettings
        {
            WebhookUrl = server.Url,
            WebhookType = "generic",
            WebhookTemplate = "{\"text\":{text},\"base64\":{imageBase64},\"uri\":{imageDataUri},\"name\":{imageFileName},\"type\":{imageContentType}}",
            WebhookTimeout = 5,
        };
        var image = new NotificationImage(
            "screenshot-generic",
            "run-1-s1.jpg",
            "image/jpeg",
            new byte[] { 0x10, 0x20, 0x30 },
            320,
            240,
            DateTimeOffset.Now);

        bool ok = await WebhookSender.SendAsync(
            settings,
            "通知正文",
            new OutboundHttpClientProvider(() => settings),
            image);
        byte[] request = await server.Completion;
        string body = Encoding.UTF8.GetString(request);

        Assert.True(ok);
        Assert.Contains("\"text\":\"通知正文\"", body, StringComparison.Ordinal);
        Assert.Contains($"\"base64\":\"{Convert.ToBase64String(image.Data)}\"", body, StringComparison.Ordinal);
        Assert.Contains($"\"uri\":\"data:image/jpeg;base64,{Convert.ToBase64String(image.Data)}\"", body, StringComparison.Ordinal);
        Assert.Contains($"\"name\":\"{image.FileName}\"", body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"image/jpeg\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenericWebhookClearsImagePlaceholdersWithoutImage()
    {
        await using var server = new CapturingHttpServer(1, "", 204);
        var settings = new AppSettings
        {
            WebhookUrl = server.Url,
            WebhookType = "generic",
            WebhookTemplate = "{\"text\":{text},\"base64\":{imageBase64},\"uri\":{imageDataUri},\"name\":{imageFileName},\"type\":{imageContentType}}",
            WebhookTimeout = 5,
        };

        bool ok = await WebhookSender.SendAsync(
            settings,
            "普通通知",
            new OutboundHttpClientProvider(() => settings));
        byte[] request = await server.Completion;
        string body = Encoding.UTF8.GetString(request);

        Assert.True(ok);
        Assert.Contains("\"text\":\"普通通知\"", body, StringComparison.Ordinal);
        Assert.Contains("\"base64\":\"\"", body, StringComparison.Ordinal);
        Assert.Contains("\"uri\":\"\"", body, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"\"", body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"\"", body, StringComparison.Ordinal);
    }

    private sealed class CapturingHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly int _expectedRequests;
        private readonly string _responseBody;
        private readonly int _statusCode;
        private readonly TaskCompletionSource<byte[]> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _run;

        public CapturingHttpServer(int expectedRequests, string responseBody, int statusCode)
        {
            _expectedRequests = expectedRequests;
            _responseBody = responseBody;
            _statusCode = statusCode;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Url = $"http://127.0.0.1:{port}/hook";
            _run = CaptureAsync();
        }

        public string Url { get; }

        public Task<byte[]> Completion => _completion.Task;

        private async Task CaptureAsync()
        {
            using var all = new MemoryStream();
            try
            {
                for (int index = 0; index < _expectedRequests; index++)
                {
                    using TcpClient client = await _listener.AcceptTcpClientAsync();
                    using NetworkStream stream = client.GetStream();
                    byte[] request = await ReadRequestAsync(stream);
                    all.Write(request);
                    byte[] body = Encoding.UTF8.GetBytes(_responseBody);
                    string reason = _statusCode switch
                    {
                        204 => "No Content",
                        200 => "OK",
                        _ => "Response",
                    };
                    string response = $"HTTP/1.1 {_statusCode} {reason}\r\n"
                        + "Content-Type: application/json\r\n"
                        + $"Content-Length: {body.Length}\r\n"
                        + "Connection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
                    if (body.Length > 0)
                    {
                        await stream.WriteAsync(body);
                    }
                }
                _completion.TrySetResult(all.ToArray());
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
        }

        private static async Task<byte[]> ReadRequestAsync(NetworkStream stream)
        {
            using var request = new MemoryStream();
            byte[] one = new byte[1];
            int headerLength = -1;
            while (request.Length < 64 * 1024)
            {
                int read = await stream.ReadAsync(one);
                if (read == 0)
                {
                    break;
                }
                request.WriteByte(one[0]);
                if (request.Length >= 4)
                {
                    byte[] bytes = request.GetBuffer();
                    int length = checked((int)request.Length);
                    if (bytes[length - 4] == '\r'
                        && bytes[length - 3] == '\n'
                        && bytes[length - 2] == '\r'
                        && bytes[length - 1] == '\n')
                    {
                        headerLength = length;
                        break;
                    }
                }
            }
            if (headerLength < 0)
            {
                return request.ToArray();
            }

            string headers = Encoding.ASCII.GetString(request.GetBuffer(), 0, headerLength);
            int contentLength = 0;
            foreach (string line in headers.Split("\r\n", StringSplitOptions.None))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    _ = int.TryParse(line[15..].Trim(), out contentLength);
                }
            }
            byte[] body = new byte[Math.Max(0, contentLength)];
            int offset = 0;
            while (offset < body.Length)
            {
                int read = await stream.ReadAsync(body.AsMemory(offset));
                if (read == 0)
                {
                    break;
                }
                offset += read;
            }
            request.Write(body, 0, offset);
            return request.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await _run.ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }
}
