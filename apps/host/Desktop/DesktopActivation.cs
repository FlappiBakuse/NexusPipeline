using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Host.Desktop;

[SupportedOSPlatform("windows")]
internal sealed class DesktopActivation : IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _stop = new();
    private readonly Action _activate;
    private readonly Task _loop;

    private DesktopActivation(NamedPipeServerStream pipe, Action activate)
    { _pipe = pipe; _activate = activate; _loop = RunAsync(); }

    internal static async Task<DesktopActivation?> OpenAsync(Guid instanceId, Action activate)
    {
        var name = "NexusActivate-" + instanceId.ToString("N");
        NamedPipeServerStream server;
        try { server = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); }
        catch (IOException)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(deadline.Token);
            if (!GetNamedPipeServerProcessId(client.SafePipeHandle, out var processId)) throw new IOException("desktop_activation_peer_missing");
            RequirePeer(processId);
            await client.WriteAsync(new byte[] { 1 }, deadline.Token);
            var response = new byte[1]; await client.ReadExactlyAsync(response, deadline.Token);
            if (response[0] != 1) throw new IOException("desktop_activation_rejected");
            return null;
        }
        return new(server, activate);
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await _pipe.WaitForConnectionAsync(_stop.Token);
                if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var processId)) throw new IOException("desktop_activation_peer_missing");
                RequirePeer(processId);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                var request = new byte[1]; await _pipe.ReadExactlyAsync(request, deadline.Token);
                if (request[0] != 1) throw new IOException("desktop_activation_rejected");
                _activate(); await _pipe.WriteAsync(new byte[] { 1 }, deadline.Token);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or System.ComponentModel.Win32Exception)
            { if (!_stop.IsCancellationRequested) Console.Error.WriteLine("desktop_activation_request_rejected"); }
            finally { if (!_stop.IsCancellationRequested && _pipe.IsConnected) _pipe.Disconnect(); }
        }
    }

    private static void RequirePeer(int processId)
    {
        var peer = ProcessIdentityReader.Read(processId);
        var current = ProcessIdentityReader.Read(Environment.ProcessId);
        if (peer.UserSid != current.UserSid || peer.IsElevated != current.IsElevated ||
            peer.ImagePath != current.ImagePath || peer.ImageSha256 != current.ImageSha256)
            throw new IOException("desktop_activation_peer_rejected");
    }

    public void Dispose() { _stop.Cancel(); _pipe.Dispose(); }
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out int processId);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out int processId);
}
