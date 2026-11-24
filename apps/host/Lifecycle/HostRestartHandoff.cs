using NexusPipeline.Core.Maintenance;
using NexusPipeline.Host.Composition;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Maintenance;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusPipeline.Host.Lifecycle;

[SupportedOSPlatform("windows")]
internal static class HostRestartStart
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var stage = "arguments";
        Guid operation = Guid.Empty;
        RuntimeInstance? observed = null;
        try
        {
            if (args.Length != 5 || !Guid.TryParseExact(args[2], "D", out operation) || operation == Guid.Empty ||
                args[2] != operation.ToString("D") || !int.TryParse(args[3], out var parentPid) || parentPid <= 0 ||
                !args[4].StartsWith("NexusRestart-", StringComparison.Ordinal) ||
                !Guid.TryParseExact(args[4][13..], "N", out _)) throw Invalid();
            stage = "parent-identity";
            var parent = ProcessIdentityReader.Read(parentPid);
            var current = ProcessIdentityReader.Read(Environment.ProcessId);
            if (parent.UserSid != current.UserSid || parent.IsElevated != current.IsElevated ||
                parent.ImagePath != current.ImagePath || parent.ImageSha256 != current.ImageSha256) throw Invalid();
            using var producer = new NativeClientBinding(parent);
            using var pipe = new NamedPipeClientStream(".", args[4], PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            stage = "pipe-connect";
            await pipe.ConnectAsync(deadline.Token);
            MaintenanceStartupOrigin.RequirePipeParent(pipe, producer, parent);
            stage = "frame-validation";
            var bytes = await MaintenanceStartupFrames.ReadAsync(pipe, deadline.Token);
            using var parsed = StrictJson.Parse(bytes);
            var frame = parsed.RootElement.Deserialize<HostRestartFrame>(HostRestartHandoff.Json) ?? throw Invalid();
            var instance = new InstanceRoot(new ContractReader()).OpenExisting(args[1]);
            if (frame.Format != "nexus.host.restart" || frame.Version != 1 || frame.OperationId != operation ||
                frame.InstanceId != instance.InstanceId || frame.Parent != parent) throw Invalid();
            observed = instance;
            stage = "job-handoff";
            using var origin = frame.JobHandle is { } handle ? MaintenanceStartupOrigin.ReceiveRestartJob(producer, parent, handle) : null;
            await pipe.WriteAsync(new byte[] { 2 }, deadline.Token);
            await pipe.FlushAsync(deadline.Token);
            var commit = new byte[1];
            await pipe.ReadExactlyAsync(commit, deadline.Token);
            if (commit[0] != 3) throw Invalid();
            stage = "parent-exit";
            await producer.WaitForExitAsync(deadline.Token);
            origin?.RequireReleasedSuccessor();
            pipe.Dispose(); producer.Dispose();
            stage = "desktop-start";
            return await NexusPipeline.Host.Desktop.DesktopStartup.RunAsync(instance.RuntimeRoot, null, maintenanceOrigin: origin, resumeWindow: frame.Window);
        }
        catch (Exception error) when (error is IOException or ArgumentException or JsonException or
            System.ComponentModel.Win32Exception or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException)
        {
            if (observed is not null) MaintenanceDiagnostic.Record(observed, operation, "restart", stage, error);
            else
            {
                try
                {
                    Console.Error.WriteLine(JsonSerializer.Serialize(new { kind = "host_restart_startup_rejected",
                        operationId = operation, stage, causes = ExecutionFailureTrace.Describe(error) }));
                }
                catch (Exception outputFailure) when (outputFailure is IOException or ObjectDisposedException) { }
            }
            return 1;
        }
    }

    private static IOException Invalid() => new("host_restart_handoff_invalid");
}

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
