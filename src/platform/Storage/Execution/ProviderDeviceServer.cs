using Microsoft.Win32.SafeHandles;
using NexusPipeline.Extensibility.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

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
        {
            _pipe.Dispose();
            if (error is IOException && error.Message is
                "execution_device_arguments_invalid" or "execution_device_sequence_invalid" or "execution_project_peer_unbound" or
                "execution_device_configuration_frozen" or "execution_capture_mapping_unverified" or "execution_mumu_mapping_unproven" or
                "execution_device_command_deadline" or "execution_device_output_limit" or "execution_device_output_rejected")
                Console.Error.WriteLine("provider_device_denied " + error.Message);
        }
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

internal static class ProviderDevicePolicy
{
    internal static void Require(ExecutionDeviceRequest request)
    {
        if (request.Kind == "configure" && (request.CaptureMethod == "adb"
            ? request.MouseMethod != "adb" || request.KeyboardMethod != "adb"
            : request.CaptureMethod is not ("printWindow" or "gdi" or "screenDc" or "framePool") ||
            request.MouseMethod is not ("sendMessage" or "postMessage" or "sendMessageWithCursorPos" or "seize") ||
            request.KeyboardMethod is not ("sendMessage" or "postMessage" or "sendMessageWithCursorPos" or "seize")))
            throw new IOException("execution_device_methods_invalid");
        string[] arguments = request.Kind switch {
            "configure" => ["captureMethod", "mouseMethod", "keyboardMethod"],
            "connect" or "connected" or "features" or "capture" or "inactive" => [],
            "image" => ["frameId", "offset"], "click" => ["x", "y"],
            "swipe" => ["x", "y", "x2", "y2", "durationMilliseconds"],
            "touch" => request.Phase == 2 ? ["phase", "contact"] : ["phase", "contact", "x", "y", "pressure"],
            "key" => ["phase", "keyCode"], "text" => ["text"], "scroll" or "move" => ["dx", "dy"],
            "startApp" or "stopApp" => ["intent"], "shell" => ["command", "timeoutMilliseconds"],
            _ => throw new IOException("execution_device_action_invalid") };
        var fields = JsonSerializer.SerializeToElement(request).EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        if (!fields.SetEquals(arguments.Prepend("kind").Prepend("sequence")) || request.Sequence is < 1 or > 100000 ||
            request.Text?.Contains('\0') == true || request.Intent?.Any(char.IsControl) == true || request.Command?.Contains('\0') == true)
            throw new IOException("execution_device_arguments_invalid");
    }
}
