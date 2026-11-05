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
    ProcessIdentity Parent, long JobHandle, UpdateWindowIntent? Window);

[SupportedOSPlatform("windows")]
internal sealed class HostRestartHandoff : IDisposable
{
    private readonly MaintenanceStartupJob _job;
    private readonly NamedPipeServerStream _pipe;
    private readonly string _pipeName = "NexusRestart-" + Guid.NewGuid().ToString("N");
    private ProcessIdentity? _child;
    internal HostRestartReply Reply { get; private set; } = null!;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    internal HostRestartHandoff(MaintenanceStartupOrigin origin)
    {
        _job = new(origin, _ => { });
        try
        {
            _pipe = new(_pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        }
        catch { _job.Dispose(); throw; }
    }

    internal async Task PrepareAsync(RuntimeInstance instance, Guid operationId, int port,
        UpdateWindowIntent? window, CancellationToken cancellation)
    {
        var parent = ProcessIdentityReader.Read(Environment.ProcessId);
        var arguments = new[] { "restart-start", instance.RuntimeRoot, operationId.ToString("D"),
            parent.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), _pipeName };
        var root = _job.Start(parent.ImagePath, arguments, Path.GetPathRoot(parent.ImagePath)!, instance.InstanceId,
            operationId, _job.RequirePersistingRoot, role: MaintenanceStartupRole.HostRoot);
        _child = new(root.ProcessId, root.CreatedAtUtc, root.ImagePath, root.ImageSha256, root.UserSid, root.IsElevated);
        using var child = new NativeClientBinding(_child);
        await _pipe.WaitForConnectionAsync(cancellation);
        using var offered = _job.RetainSuccessorJob(_child);
        var frame = new HostRestartFrame("nexus.host.restart", 1, operationId, instance.InstanceId, parent, offered.ExportValue, window);
        await MaintenanceStartupFrames.WriteAsync(_pipe, BoundedJson.Serialize(frame, Json), cancellation);
        await MaintenanceStartupOrigin.AcknowledgeSuccessorAsync(_pipe, _job, offered, child, _child, cancellation);
        var ready = new byte[1];
        await _pipe.ReadExactlyAsync(ready, cancellation);
        if (ready[0] != 2 || !child.IsRegistered(_child)) throw new IOException("restart_successor_not_ready");
        Reply = new(operationId, instance.InstanceId, root.ProcessId, port, "prepared");
    }

    internal async Task CommitAsync(CancellationToken cancellation)
    {
        _job.ReleasePreparedRoot(_child ?? throw new IOException("restart_successor_missing"));
        await _pipe.WriteAsync(new byte[] { 3 }, cancellation);
        await _pipe.FlushAsync(cancellation);
        Reply = Reply with { State = "handoff" };
    }

    internal Task<bool> StopAsync() => _job.StopAsync(TimeSpan.FromSeconds(10));
    public void Dispose()
    {
        _pipe.Dispose();
        _job.Dispose();
    }
}
