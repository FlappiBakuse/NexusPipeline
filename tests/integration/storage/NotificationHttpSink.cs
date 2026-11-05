using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace NexusPipeline.StorageTests;

internal sealed record NotificationSinkRequest(string Target, IReadOnlyDictionary<string, string> Headers, long BodyBytes, string Sha256, string Prefix);
internal sealed record NotificationSinkReply(int Status, string Body);

internal sealed class NotificationHttpSink : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly Func<NotificationSinkRequest, CancellationToken, Task<NotificationSinkReply>> _reply;
    private readonly Task _accepting;
    internal ConcurrentQueue<NotificationSinkRequest> Requests { get; } = [];
    internal Uri Address { get; }

    internal NotificationHttpSink(Func<NotificationSinkRequest, CancellationToken, Task<NotificationSinkReply>> reply)
    {
        _reply = reply; _listener.Start();
        Address = new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        _accepting = Accept();
    }

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
            try
            {
                await using var stream = socket.GetStream();
                var reader = new SocketInput(stream);
                var first = (await reader.Line(_stopping.Token)).Split(' ');
                if (first.Length != 3 || first[0] != "POST") throw new IOException("notification_sink_method");
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var count = 0; ; count++)
                {
                    var line = await reader.Line(_stopping.Token);
                    if (line.Length == 0) break;
                    if (count >= 100) throw new IOException("notification_sink_headers");
                    var colon = line.IndexOf(':');
                    if (colon < 1 || !headers.TryAdd(line[..colon], line[(colon + 1)..].Trim())) throw new IOException("notification_sink_headers");
                }
                if (!headers.TryGetValue("Content-Length", out var text) || !long.TryParse(text, out var length) || length is < 0 or > 33554432)
                    throw new IOException("notification_sink_body_bound");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var prefix = new MemoryStream(); var remaining = length; var buffer = new byte[16384];
                while (remaining > 0)
                {
                    var count = await reader.Read(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), _stopping.Token);
                    if (count == 0) throw new IOException("notification_sink_body_incomplete");
                    hash.AppendData(buffer, 0, count); remaining -= count;
                    if (prefix.Length < 65536) prefix.Write(buffer, 0, (int)Math.Min(count, 65536 - prefix.Length));
                }
                var request = new NotificationSinkRequest(first[1], headers, length, Convert.ToHexStringLower(hash.GetHashAndReset()),
                    Encoding.UTF8.GetString(prefix.GetBuffer(), 0, (int)prefix.Length));
                Requests.Enqueue(request);
                var reply = await _reply(request, _stopping.Token);
                var body = Encoding.UTF8.GetBytes(reply.Body);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {reply.Status} Fixture\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), _stopping.Token);
                await stream.WriteAsync(body, _stopping.Token); await stream.FlushAsync(_stopping.Token);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            catch (IOException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync(); await _accepting; _listener.Stop();
        await Task.WhenAll(_connections.ToArray()).WaitAsync(TimeSpan.FromSeconds(10)); _stopping.Dispose();
    }

    private sealed class SocketInput(Stream stream)
    {
        private readonly byte[] _buffer = new byte[8192];
        private int _position, _count;
        internal async ValueTask<int> Read(Memory<byte> target, CancellationToken cancellation)
        {
            if (_position == _count)
            {
                _count = await stream.ReadAsync(_buffer, cancellation); _position = 0;
                if (_count == 0) return 0;
            }
            var count = Math.Min(target.Length, _count - _position);
            _buffer.AsMemory(_position, count).CopyTo(target); _position += count; return count;
        }
        internal async Task<string> Line(CancellationToken cancellation)
        {
            var bytes = new List<byte>(); var single = new byte[1];
            while (true)
            {
                if (await Read(single, cancellation) == 0) throw new IOException("notification_sink_line_incomplete");
                if (single[0] == 10)
                {
                    if (bytes.Count == 0 || bytes[^1] != 13) throw new IOException("notification_sink_crlf");
                    return Encoding.ASCII.GetString(bytes.ToArray(), 0, bytes.Count - 1);
                }
                if (bytes.Count >= 16384) throw new IOException("notification_sink_line_limit");
                bytes.Add(single[0]);
            }
        }
    }
}
