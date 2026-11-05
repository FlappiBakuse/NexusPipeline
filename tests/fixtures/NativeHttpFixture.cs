using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace NexusPipeline.TestFixtures;

internal sealed record NativeHttpRequest(string Target, IReadOnlyDictionary<string, string> Headers);
internal sealed record NativeHttpReply(int Status, byte[] Body, IReadOnlyDictionary<string, string>? Headers = null);

internal sealed class NativeHttpFixture : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly Func<NativeHttpRequest, CancellationToken, Task<NativeHttpReply>> _reply;
    private readonly X509Certificate2? _certificate;
    private readonly Task _accepting;
    internal ConcurrentQueue<NativeHttpRequest> Requests { get; } = [];
    internal Uri Address { get; }

    internal NativeHttpFixture(Func<NativeHttpRequest, CancellationToken, Task<NativeHttpReply>> reply, bool tls = false)
    {
        _reply = reply;
        if (tls)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            _certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        }
        _listener.Start();
        Address = new($"{(tls ? "https" : "http")}://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        _accepting = Accept();
    }

    internal NativeHttpFixture(Func<NativeHttpRequest, NativeHttpReply> reply, bool tls = false)
        : this((request, _) => Task.FromResult(reply(request)), tls) { }

    private async Task Accept()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                var socket = await _listener.AcceptTcpClientAsync(_stopping.Token);
                _connections.Add(Serve(socket));
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
    }

    private async Task Serve(TcpClient socket)
    {
        using (socket)
        {
            Stream stream = socket.GetStream();
            try
            {
                if (_certificate is not null)
                {
                    var ssl = new SslStream(stream, leaveInnerStreamOpen: false); stream = ssl;
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, _stopping.Token);
                }
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var first = await reader.ReadLineAsync(_stopping.Token) ?? throw new IOException("fixture_request_missing");
                var parts = first.Split(' ');
                if (parts.Length != 3 || parts[0] != "GET") throw new InvalidDataException("fixture_request_invalid");
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var count = 0; ; count++)
                {
                    var line = await reader.ReadLineAsync(_stopping.Token) ?? throw new IOException("fixture_headers_incomplete");
                    if (line.Length == 0) break;
                    if (count == 100 || line.Length > 8192) throw new InvalidDataException("fixture_headers_oversized");
                    var colon = line.IndexOf(':');
                    if (colon < 1 || !headers.TryAdd(line[..colon], line[(colon + 1)..].Trim())) throw new InvalidDataException("fixture_headers_invalid");
                }
                var request = new NativeHttpRequest(parts[1], headers); Requests.Enqueue(request);
                var reply = await _reply(request, _stopping.Token);
                var head = $"HTTP/1.1 {reply.Status} Fixture\r\nContent-Length: {reply.Body.Length}\r\nConnection: close\r\n";
                if (reply.Headers is not null) head += string.Concat(reply.Headers.Select(header => header.Key + ": " + header.Value + "\r\n"));
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head + "\r\n"), _stopping.Token);
                await stream.WriteAsync(reply.Body, _stopping.Token); await stream.FlushAsync(_stopping.Token);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            catch (IOException) { }
            catch (AuthenticationException) when (_certificate is not null) { }
            finally { await stream.DisposeAsync(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        await _accepting;
        _listener.Stop();
        await Task.WhenAll(_connections.ToArray()).WaitAsync(TimeSpan.FromSeconds(10));
        _certificate?.Dispose(); _stopping.Dispose();
    }
}
