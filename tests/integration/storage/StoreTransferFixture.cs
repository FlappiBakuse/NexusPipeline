using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NexusPipeline.StorageTests;

internal sealed class StoreTransferFixture : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _serving;
    internal Uri Address { get; }
    internal TaskCompletionSource BodyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal StoreTransferFixture(string headers, byte[] body, bool chunked = false, bool suspendBody = false)
    {
        _listener.Start();
        Address = new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        _serving = Serve(headers, body, chunked, suspendBody);
    }

    private async Task Serve(string headers, byte[] body, bool chunked, bool suspendBody)
    {
        try
        {
            using var socket = await _listener.AcceptTcpClientAsync(_stopping.Token);
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            for (var index = 0; ; index++)
            {
                var line = await reader.ReadLineAsync(_stopping.Token);
                if (line is null || line.Length == 0) break;
                if (index > 100 || line.Length > 8192) throw new IOException("fixture_request_oversized");
            }
            var head = "HTTP/1.1 200 Fixture\r\nConnection: close\r\n" + headers +
                (chunked ? "Transfer-Encoding: chunked\r\n" : "") + "\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), _stopping.Token);
            BodyStarted.TrySetResult();
            if (suspendBody) await Task.Delay(Timeout.Infinite, _stopping.Token);
            if (chunked)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(body.Length.ToString("x") + "\r\n"), _stopping.Token);
                await stream.WriteAsync(body, _stopping.Token);
                await stream.WriteAsync("\r\n0\r\n\r\n"u8.ToArray(), _stopping.Token);
            }
            else await stream.WriteAsync(body, _stopping.Token);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (IOException) { }
        finally { BodyStarted.TrySetCanceled(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync(); _listener.Stop();
        await _serving.WaitAsync(TimeSpan.FromSeconds(10)); _stopping.Dispose();
    }
}
