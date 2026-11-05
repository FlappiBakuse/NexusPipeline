using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using NexusPipeline.Extensibility.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class ProviderDeviceServer : IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly ContractReader _contracts;
    private readonly Action<uint> _requirePeer;
    private readonly Func<ExecutionDeviceRequest, CancellationToken, ValueTask<ExecutionDeviceResponse>> _invoke;
    private readonly CancellationTokenSource _lifetime;
    private readonly Task _receive;

    internal ProviderDeviceServer(string pipeName, ContractReader contracts, Action<uint> requirePeer,
        Func<ExecutionDeviceRequest, CancellationToken, ValueTask<ExecutionDeviceResponse>> invoke, CancellationToken lifetime)
    {
        _contracts = contracts; _requirePeer = requirePeer; _invoke = invoke;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _pipe = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _receive = ReceiveAsync();
    }
    private async Task ReceiveAsync()
    {
        try
        {
            await _pipe.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
            RequirePeer(); await ExecutionProviderFrames.WriteAsync(_pipe, new byte[] { 1 }, 1, _lifetime.Token).ConfigureAwait(false);
            long sequence = 0, transferred = 0;
            while (true)
            {
                var bytes = await ExecutionProviderFrames.ReadAsync(_pipe, 65536, _lifetime.Token).ConfigureAwait(false);
                RequirePeer();
                var request = _contracts.Read<ExecutionDeviceRequest>(bytes, "execution-device-request.schema.json");
                if (request.Sequence != ++sequence) throw new IOException("execution_device_sequence_invalid");
                ProviderDevicePolicy.Require(request);
                var response = await _invoke(request, _lifetime.Token).ConfigureAwait(false);
                if (response.Sequence != sequence || response.Kind != request.Kind) throw new IOException("execution_device_response_unbound");
                RequirePeer();
                var encoded = ExecutionProviderFrames.Encode(response, 262144);
                transferred = checked(transferred + encoded.Length);
                if (transferred > 4L * 1024 * 1024 * 1024) throw new IOException("execution_device_transfer_limit");
                using var validated = _contracts.Read(encoded, "execution-device-response.schema.json");
                await ExecutionProviderFrames.WriteAsync(_pipe, encoded, 262144, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException or ObjectDisposedException or
            System.Text.Json.JsonException or ArgumentException or InvalidOperationException or OverflowException or System.ComponentModel.Win32Exception)
        { _pipe.Dispose(); }
    }
    private void RequirePeer()
    {
        _lifetime.Token.ThrowIfCancellationRequested();
        if (!ClientProcess(_pipe.SafePipeHandle, out var pid)) throw new IOException("execution_device_peer_unbound");
        _requirePeer(pid);
    }
    internal async Task CloseAsync()
    { _lifetime.Cancel(); _pipe.Dispose(); await _receive.ConfigureAwait(false); }
    public void Dispose()
    {
        if (!_receive.IsCompleted) throw new IOException("execution_device_collection_pending");
        _pipe.Dispose(); _lifetime.Dispose();
    }
    [DllImport("kernel32.dll", EntryPoint = "GetNamedPipeClientProcessId", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientProcess(SafePipeHandle pipe, out uint pid);
}
