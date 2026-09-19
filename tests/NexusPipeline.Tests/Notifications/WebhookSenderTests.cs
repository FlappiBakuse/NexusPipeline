using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Modules.Notifications;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Platform.Networking;

namespace NexusPipeline.Tests.Notifications;

public sealed class WebhookSenderTests
{
    [Theory]
    [InlineData("feishu", "{\"msg_type\":\"text\",\"content\":{\"text\":\"正文\"}}")]
    [InlineData("dingtalk", "{\"msgtype\":\"text\",\"text\":{\"content\":\"正文\"}}")]
    [InlineData("wecom", "{\"msgtype\":\"text\",\"text\":{\"content\":\"正文\"}}")]
    [InlineData("slack", "{\"text\":\"正文\"}")]
    [InlineData("discord", "{\"content\":\"正文\"}")]
    public void BuildBody_UsesTargetSpecificPayload(string type, string expected)
    {
        Assert.Equal(expected, WebhookSender.BuildBody(type, "正文", ""));
    }

    [Fact]
    public void GenericBody_ExpandsAllImagePlaceholders()
    {
        var image = new NotificationImage(
            "id",
            "capture.jpg",
            "image/jpeg",
            new byte[] { 1, 2, 3 },
            1,
            1,
            DateTimeOffset.UtcNow);

        string body = WebhookSender.BuildGenericBody(
            "正文",
            "{\"text\":{text},\"base64\":{imageBase64},\"uri\":{imageDataUri},\"name\":{imageFileName},\"type\":{imageContentType}}",
            image);

        Assert.Contains("\"text\":\"正文\"", body, StringComparison.Ordinal);
        Assert.Contains("\"base64\":\"AQID\"", body, StringComparison.Ordinal);
        Assert.Contains("data:image/jpeg;base64,AQID", body, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"capture.jpg\"", body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"image/jpeg\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Signatures_UseExpectedTargetLocations()
    {
        (string dingtalkUrl, Dictionary<string, string> dingtalkHeaders) = WebhookSender.ApplySignature(
            "dingtalk",
            "https://example.test/hook",
            "secret");
        Assert.Empty(dingtalkHeaders);
        Uri signedUrl = new(dingtalkUrl);
        Dictionary<string, string> query = signedUrl.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(part => part[0], part => WebUtility.UrlDecode(part[1]), StringComparer.OrdinalIgnoreCase);
        Assert.True(query.ContainsKey("timestamp"));
        Assert.Equal(WebhookSender.Sign(query["timestamp"], "secret"), query["sign"]);

        string feishuBody = WebhookSender.BuildSignedFeishuBody(
            WebhookSender.BuildBody("feishu", "正文", ""),
            "secret");
        using JsonDocument document = JsonDocument.Parse(feishuBody);
        string timestamp = document.RootElement.GetProperty("timestamp").GetString()!;
        Assert.Equal(WebhookSender.Sign(timestamp, "secret"), document.RootElement.GetProperty("sign").GetString());
    }

    [Theory]
    [InlineData("feishu", "{\"code\":0}", true)]
    [InlineData("feishu", "{\"code\":99}", false)]
    [InlineData("dingtalk", "{\"errcode\":0}", true)]
    [InlineData("dingtalk", "{\"success\":false}", false)]
    [InlineData("wecom", "{\"errcode\":0}", true)]
    [InlineData("wecom", "{\"errcode\":1}", false)]
    [InlineData("slack", "{\"ok\":false}", true)]
    public void ResponseBodyStatus_IsMappedByProvider(string type, string body, bool expected)
    {
        Assert.Equal(expected, WebhookSender.IsResponseBodySuccessful(type, body));
    }

    [Theory]
    [InlineData("feishu", "{\"code\":0}", "\"msg_type\":\"text\"")]
    [InlineData("dingtalk", "{\"errcode\":0}", "\"msgtype\":\"text\"")]
    [InlineData("slack", "", "\"text\":\"正文\"")]
    public async Task TextWebhook_SendsPayloadAndSignature(string type, string response, string expectedRequestPart)
    {
        await using var server = new OneRequestServer(response, 200);
        var settings = new AppSettings
        {
            WebhookUrl = server.Url,
            WebhookType = type,
            WebhookSecret = "secret",
            WebhookTimeout = 5,
        };

        bool ok = await WebhookSender.SendAsync(
            settings,
            "正文",
            new OutboundHttpClientProvider(() => new OutboundProxyOptions(settings.ProxyMode, settings.ProxyUrl, settings.ProxyUsername, settings.ProxyPassword)));
        string request = Encoding.UTF8.GetString(await server.Completion);

        Assert.True(ok);
        Assert.Contains(expectedRequestPart, request, StringComparison.Ordinal);
        if (type == "dingtalk")
        {
            Assert.Contains("timestamp=", request, StringComparison.Ordinal);
            Assert.Contains("sign=", request, StringComparison.Ordinal);
        }
        if (type == "feishu")
        {
            Assert.Contains("\"timestamp\":", request, StringComparison.Ordinal);
            Assert.Contains("\"sign\":", request, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task InvalidWebhookUri_ReturnsFalseWithoutThrowing()
    {
        var settings = new AppSettings
        {
            WebhookUrl = "not a uri",
            WebhookType = "generic",
            WebhookTemplate = "{\"text\":{text}}",
        };

        Assert.False(await WebhookSender.SendAsync(settings, "正文"));
    }

    private sealed class OneRequestServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _responseBody;
        private readonly int _statusCode;
        private readonly TaskCompletionSource<byte[]> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _run;

        public OneRequestServer(string responseBody, int statusCode)
        {
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
            try
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync();
                using NetworkStream stream = client.GetStream();
                byte[] request = await ReadRequestAsync(stream);
                byte[] body = Encoding.UTF8.GetBytes(_responseBody);
                string reason = _statusCode == 200 ? "OK" : "Response";
                string response = $"HTTP/1.1 {_statusCode} {reason}\r\n"
                    + "Content-Type: application/json\r\n"
                    + $"Content-Length: {body.Length}\r\n"
                    + "Connection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
                await stream.WriteAsync(body);
                _completion.TrySetResult(request);
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
