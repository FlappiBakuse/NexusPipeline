using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Maintenance;

namespace NexusPipeline.Host.Lifecycle;

internal sealed record HostRestartReply(Guid OperationId, Guid InstanceId, int ProcessId, int Port, string State);
internal sealed record HostRestartFrame(string Format, int Version, Guid OperationId, Guid InstanceId,
    ProcessIdentity Parent, long? JobHandle, UpdateWindowIntent? Window);

[SupportedOSPlatform("windows")]
internal sealed class HostRestartHandoff : IDisposable
{
    private readonly string _pipeName = "NexusRestart-" + Guid.NewGuid().ToString("N");
    private readonly NamedPipeServerStream _pipe;
    private readonly MaintenanceStartupOrigin? _origin;
    private MaintenanceStartupProcess? _successor;
    internal HostRestartReply Reply { get; private set; } = null!;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    internal HostRestartHandoff(MaintenanceStartupOrigin? origin)
    {
        _origin = origin;
        _pipe = new(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
    }

    internal async Task PrepareAsync(RuntimeInstance instance, Guid operationId, int port,
        UpdateWindowIntent? window, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var parent = ProcessIdentityReader.Read(Environment.ProcessId);
        using var inherited = _origin?.RetainRestartJob();
        var arguments = new[] { "restart-start", instance.RuntimeRoot, operationId.ToString("D"),
            parent.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), _pipeName };
        _successor = MaintenanceStartupProcess.CreateRestart(parent.ImagePath, arguments, Path.GetPathRoot(parent.ImagePath)!);
        var root = _successor.ReadFrozenIdentity(instance.InstanceId, operationId);
        var identity = new ProcessIdentity(root.ProcessId, root.CreatedAtUtc, root.ImagePath, root.ImageSha256, root.UserSid, root.IsElevated);
        _successor.Resume();
        using var child = new NativeClientBinding(identity);
        await _pipe.WaitForConnectionAsync(cancellation);
        MaintenanceStartupOrigin.RequirePipeChild(_pipe, child, identity);
        var frame = new HostRestartFrame("nexus.host.restart", 1, operationId, instance.InstanceId, parent, inherited?.ExportValue, window);
        await MaintenanceStartupFrames.WriteAsync(_pipe, BoundedJson.Serialize(frame, Json), cancellation);
        var ready = new byte[1];
        await _pipe.ReadExactlyAsync(ready, cancellation);
        MaintenanceStartupOrigin.RequirePipeChild(_pipe, child, identity);
        if (ready[0] != 2) throw new IOException("restart_successor_not_ready");
        Reply = new(operationId, instance.InstanceId, root.ProcessId, port, "prepared");
    }

    internal async Task CommitAsync(CancellationToken cancellation)
    {
        await _pipe.WriteAsync(new byte[] { 3 }, cancellation);
        await _pipe.FlushAsync(cancellation);
        Reply = Reply with { State = "handoff" };
    }

    internal bool Stop() => _successor?.Stop() is null;
    public void Dispose()
    {
        _pipe.Dispose();
        _successor?.Dispose();
    }
}
