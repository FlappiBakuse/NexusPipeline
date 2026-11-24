using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Host.Desktop;

[SupportedOSPlatform("windows")]
internal sealed class StartupChannel : IDisposable
{
    private const int MaximumFrameBytes = 16384;
    private readonly NamedPipeServerStream _pipe;
    private readonly byte[] _receive = new byte[MaximumFrameBytes + 1];
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writes = new(1);
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _dismissed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _receiveStart;
    private int _receiveEnd;
    private Task? _loop;
    private bool _ready;
    private bool _failed;
    internal string Name { get; } = "NexusDesktop-" + Guid.NewGuid().ToString("N");
    internal string Nonce { get; } = Guid.NewGuid().ToString("N");
    internal Task Dismissed => _dismissed.Task;

    internal StartupChannel()
    {
        _pipe = new(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    internal void Bind(int processId) => _loop = RunAsync(processId);

    internal async Task SendAsync(object value)
    {
        await _connected.Task.WaitAsync(_stop.Token);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumFrameBytes) throw new IOException("desktop_startup_frame_limit");
        await _writes.WaitAsync(_stop.Token);
        try { await _pipe.WriteAsync(bytes, _stop.Token); await _pipe.WriteAsync(new byte[] { 10 }, _stop.Token); await _pipe.FlushAsync(_stop.Token); }
        finally { _writes.Release(); }
    }

    internal async Task ReadyAsync(object config)
    {
        _ready = true;
        await SendAsync(new { kind = "ready", config });
    }

    internal async Task FailAsync(string reason, int errorCode)
    {
        _failed = true; _ready = false;
        await SendAsync(new { kind = "failed", reason, errorCode });
    }

    internal void Publish(object value)
    {
        _ = PublishAsync(value);
    }

    private async Task PublishAsync(object value)
    {
        try { await SendAsync(value); }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        { Console.Error.WriteLine("desktop_startup_channel_unavailable"); }
    }

    private async Task RunAsync(int processId)
    {
        try
        {
            await _pipe.WaitForConnectionAsync(_stop.Token);
            if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var peer) || peer != processId)
                throw new IOException("desktop_startup_peer_rejected");
            using var first = StrictJson.Parse(await ReadAsync());
            if (first.RootElement.GetProperty("kind").GetString() != "hello" || first.RootElement.GetProperty("nonce").GetString() != Nonce)
                throw new IOException("desktop_startup_handshake_rejected");
            _connected.TrySetResult();
            while (!_stop.IsCancellationRequested)
            {
                using var frame = StrictJson.Parse(await ReadAsync());
                var value = frame.RootElement;
                switch (value.GetProperty("kind").GetString())
                {
                    case "menu":
                        var id = value.GetProperty("id").GetInt32();
                        var action = await NativeTrayMenu.ShowAsync(value.GetProperty("browser").GetBoolean(),
                            value.GetProperty("busy").GetBoolean(), _ready, _failed, value.GetProperty("locale").GetString() == "zh-CN");
                        await SendAsync(new { kind = "menu", id, action });
                        break;
                    case "dismiss" when _failed: _dismissed.TrySetResult(); break;
                    case "presented":
                        Console.Error.WriteLine(JsonSerializer.Serialize(new { kind = "startup.presentation", surface = value.GetProperty("surface").GetString() }));
                        break;
                    case "measurement":
                        var stage = value.GetProperty("stage").GetString();
                        var elapsed = value.GetProperty("elapsedMilliseconds").GetDouble();
                        if (stage is not ("session" or "document" or "interface") || !double.IsFinite(elapsed) || elapsed < 0 || elapsed > 3600000)
                            throw new IOException("desktop_startup_measurement_rejected");
                        Console.Error.WriteLine(JsonSerializer.Serialize(new { kind = "desktop.preparation", stage, elapsedMilliseconds = elapsed }));
                        break;
                    default: throw new IOException("desktop_startup_message_rejected");
                }
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            _connected.TrySetException(new IOException("desktop_startup_connection_ended"));
            _dismissed.TrySetResult();
            if (!_stop.IsCancellationRequested) Console.Error.WriteLine("desktop_startup_connection_ended");
        }
        finally { _pipe.Dispose(); }
    }

    private async Task<byte[]> ReadAsync()
    {
        var scan = _receiveStart;
        while (true)
        {
            var delimiter = Array.IndexOf(_receive, (byte)'\n', scan, _receiveEnd - scan);
            if (delimiter >= 0)
            {
                var frame = _receive.AsSpan(_receiveStart, delimiter - _receiveStart).ToArray();
                _receiveStart = delimiter + 1;
                return frame;
            }

            var length = _receiveEnd - _receiveStart;
            if (length > MaximumFrameBytes) throw new IOException("desktop_startup_frame_limit");
            if (_receiveEnd == _receive.Length)
            {
                Array.Copy(_receive, _receiveStart, _receive, 0, length);
                _receiveStart = 0;
                _receiveEnd = length;
            }

            scan = _receiveEnd;
            int read;
            using (StartupProfile.Measure("desktop.pipe.read"))
                read = await _pipe.ReadAsync(_receive.AsMemory(_receiveEnd), _stop.Token);
            if (read == 0) throw new EndOfStreamException();
            _receiveEnd += read;
        }
    }

    public void Dispose()
    {
        _stop.Cancel(); _pipe.Dispose();
        _connected.TrySetCanceled(); _dismissed.TrySetResult();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out int processId);
}
