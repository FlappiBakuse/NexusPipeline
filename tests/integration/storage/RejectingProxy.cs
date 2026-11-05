using System.Net;
using System.Net.Sockets;

namespace NexusPipeline.StorageTests;

internal sealed class RejectingProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _accepting;
    private int _connections;
    internal int Connections => Volatile.Read(ref _connections);
    internal Uri Address { get; }

    internal RejectingProxy()
    {
        _listener.Start();
        Address = new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
        _accepting = Accept();
    }

    private async Task Accept()
    {
        try
        {
            while (true)
            {
                using var socket = await _listener.AcceptTcpClientAsync(_stopping.Token);
                Interlocked.Increment(ref _connections);
                try
                {
                    await socket.GetStream().WriteAsync("HTTP/1.1 502 Fixture\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), _stopping.Token);
                }
                catch (IOException) { }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        await _accepting;
        _listener.Stop(); _stopping.Dispose();
    }
}
