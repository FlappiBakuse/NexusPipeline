using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Extensibility.Execution;

/// <summary>Uses only the target owned by the current authenticated worker invocation.</summary>
public sealed class ExecutionDeviceClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _exchange = new(1, 1), _capture = new(1, 1);
    private long _sequence;
    private bool _closed;
    private ExecutionDeviceClient(NamedPipeClientStream pipe) { _pipe = pipe; }

    /// <summary>Connects to the original Host device pipe; an arbitrary HWND or address never grants access.</summary>
    public static async Task<ExecutionDeviceClient> ConnectAsync(ExecutionProviderBootstrap bootstrap, CancellationToken cancellationToken)
    {
        var grant = bootstrap.Device ?? throw new IOException("execution_device_unavailable");
        if (bootstrap.Identity.Nonce is not { Length: 64 } || bootstrap.Identity.Nonce.Any(character => !char.IsAsciiHexDigitLower(character)) ||
            grant.PipeName != "NexusExecutionDevice-" + bootstrap.Identity.Nonce || grant.Kind is not ("pc" or "android"))
            throw new IOException("execution_device_grant_invalid");
        var pipe = new NamedPipeClientStream(".", grant.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var admitted = await ExecutionProviderFrames.ReadAsync(pipe, 1, cancellationToken).ConfigureAwait(false);
            if (admitted is not [1]) throw new IOException("execution_device_peer_unbound");
            return new(pipe);
        }
        catch { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
    }

    /// <summary>Performs one typed operation. The Host revalidates the current target and may reject unsupported operations.</summary>
    public async Task<ExecutionDeviceResponse> InvokeAsync(ExecutionDeviceRequest request, CancellationToken cancellationToken)
    {
        await _exchange.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_closed || _sequence >= 100000) throw new IOException("execution_device_closed");
            request = request with { Sequence = ++_sequence };
            try
            {
                await ExecutionProviderFrames.WriteAsync(_pipe, ExecutionProviderFrames.Encode(request, 65536), 65536, cancellationToken).ConfigureAwait(false);
                var bytes = await ExecutionProviderFrames.ReadAsync(_pipe, 262144, cancellationToken).ConfigureAwait(false);
                var response = JsonSerializer.Deserialize<ExecutionDeviceResponse>(bytes) ?? throw new IOException("execution_device_response_invalid");
                if (response.Sequence != request.Sequence || response.Kind != request.Kind) throw new IOException("execution_device_response_unbound");
                return response;
            }
            catch { _closed = true; await _pipe.DisposeAsync().ConfigureAwait(false); throw; }
        }
        finally { _exchange.Release(); }
    }

    /// <summary>Reads one exact original-size PNG through bounded chunks tied to its fresh frame identity and digest.</summary>
    public async Task<ReadOnlyMemory<byte>> CaptureAsync(CancellationToken cancellationToken)
    {
        await _capture.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var metadata = await InvokeAsync(new() { Sequence = 1, Kind = "capture" }, cancellationToken).ConfigureAwait(false);
            if (!metadata.Success || metadata.FrameId is not { Length: 32 } || metadata.SizeBytes is not (>= 33 and <= 8388608) ||
                metadata.Width is not (>= 1 and <= 8192) || metadata.Height is not (>= 1 and <= 8192) ||
                metadata.Sha256 is not { Length: 64 }) throw new IOException("execution_device_capture_invalid");
            var png = new byte[checked((int)metadata.SizeBytes.Value)];
            for (var offset = 0; offset < png.Length;)
            {
                var part = await InvokeAsync(new() { Sequence = 1, Kind = "image", FrameId = metadata.FrameId, Offset = offset }, cancellationToken).ConfigureAwait(false);
                var length = Math.Min(131072, png.Length - offset);
                if (!part.Success || part.FrameId != metadata.FrameId || part.Sha256 != metadata.Sha256 || part.SizeBytes != png.Length ||
                    part.Offset != offset || part.ContentBase64 is null ||
                    !Convert.TryFromBase64String(part.ContentBase64, png.AsSpan(offset, length), out var count) || count != length)
                    throw new IOException("execution_device_frame_changed");
                offset += length;
            }
            if (!png.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                Convert.ToHexStringLower(SHA256.HashData(png)) != metadata.Sha256) throw new IOException("execution_device_frame_changed");
            return png;
        }
        finally { _capture.Release(); }
    }

    /// <summary>Revokes this client without authorizing any target shutdown.</summary>
    public ValueTask DisposeAsync() { _closed = true; return _pipe.DisposeAsync(); }
}
