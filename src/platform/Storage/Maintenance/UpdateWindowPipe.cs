using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal enum UpdateWindowCommand : byte { Read = 1, Check, Download, Cancel, Apply, Close }
internal enum UpdateWindowPhase { Idle, Current, Checking, Available, Downloading, Cancelling, Ready, HandedOff, Closed, Blocked }
internal sealed record UpdateWindowStatus(string Current, string? Available, UpdateWindowPhase Phase,
    long Received = 0, long Total = 0, string? Reason = null);

[SupportedOSPlatform("windows")]
internal static class UpdateWindowPipe
{
    private const PipeOptions Options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
    private const int MaximumFrame = 65536;

    internal static NamedPipeServerStream Listen(string root) =>
        new(Name(root), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, Options);

    internal static NamedPipeClientStream Connect(string root) => new(".", Name(root), PipeDirection.InOut, Options);

    private static string Name(string root) =>
        "NexusUpdate-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())));

    internal static int ClientProcess(NamedPipeServerStream pipe) =>
        ClientPid(pipe.SafePipeHandle, out var pid) && pid > 0 ? pid : throw Invalid();

    internal static int ServerProcess(NamedPipeClientStream pipe) =>
        ServerPid(pipe.SafePipeHandle, out var pid) && pid > 0 ? pid : throw Invalid();

    internal static async Task<UpdateWindowCommand> ReadCommandAsync(Stream pipe, CancellationToken cancellation)
    {
        var value = new byte[1];
        await pipe.ReadExactlyAsync(value, cancellation);
        var command = (UpdateWindowCommand)value[0];
        return Enum.IsDefined(command) ? command : throw Invalid();
    }

    internal static async Task<UpdateWindowStatus> RequestAsync(Stream pipe, UpdateWindowCommand command, CancellationToken cancellation)
    {
        await pipe.WriteAsync(new[] { (byte)command }, cancellation);
        var prefix = new byte[sizeof(int)];
        await pipe.ReadExactlyAsync(prefix, cancellation);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is < 1 or > MaximumFrame) throw Invalid();
        var bytes = new byte[length];
        await pipe.ReadExactlyAsync(bytes, cancellation);
        return JsonSerializer.Deserialize<UpdateWindowStatus>(bytes) ?? throw Invalid();
    }

    internal static async Task ReplyAsync(Stream pipe, UpdateWindowStatus status, CancellationToken cancellation)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(status);
        if (bytes.Length is < 1 or > MaximumFrame) throw Invalid();
        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await pipe.WriteAsync(prefix, cancellation);
        await pipe.WriteAsync(bytes, cancellation);
        await pipe.FlushAsync(cancellation);
    }

    internal static string Reason(Exception error) => error.Message.Length is > 0 and <= 100 &&
        error.Message.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
        ? error.Message : error.GetType().Name;

    internal static bool IsFailure(Exception error) => error is IOException or ArgumentException or JsonException or
        Win32Exception or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException or
        HttpRequestException or TimeoutException or AggregateException;

    private static IOException Invalid() => new("maintenance_window_peer_unbound");

    [DllImport("kernel32.dll", EntryPoint = "GetNamedPipeClientProcessId", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientPid(SafePipeHandle pipe, out int pid);

    [DllImport("kernel32.dll", EntryPoint = "GetNamedPipeServerProcessId", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ServerPid(SafePipeHandle pipe, out int pid);
}
