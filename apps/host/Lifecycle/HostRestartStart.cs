using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Host.Composition;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Maintenance;

namespace NexusPipeline.Host.Lifecycle;

[SupportedOSPlatform("windows")]
internal static class HostRestartStart
{
    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length != 5 || !Guid.TryParseExact(args[2], "D", out var operation) || operation == Guid.Empty ||
                args[2] != operation.ToString("D") || !int.TryParse(args[3], out var parentPid) || parentPid <= 0 ||
                !args[4].StartsWith("NexusRestart-", StringComparison.Ordinal) ||
                !Guid.TryParseExact(args[4][13..], "N", out _)) throw Invalid();
            var parent = ProcessIdentityReader.Read(parentPid);
            var current = ProcessIdentityReader.Read(Environment.ProcessId);
            if (parent.UserSid != current.UserSid || parent.IsElevated != current.IsElevated ||
                parent.ImagePath != current.ImagePath || parent.ImageSha256 != current.ImageSha256) throw Invalid();
            using var producer = new NativeClientBinding(parent);
            using var pipe = new NamedPipeClientStream(".", args[4], PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await pipe.ConnectAsync(deadline.Token);
            var bytes = await MaintenanceStartupFrames.ReadAsync(pipe, deadline.Token);
            using var parsed = StrictJson.Parse(bytes);
            var frame = parsed.RootElement.Deserialize<HostRestartFrame>(HostRestartHandoff.Json) ?? throw Invalid();
            var instance = new InstanceRoot(new ContractReader()).OpenExisting(args[1]);
            if (frame.Format != "nexus.host.restart" || frame.Version != 1 || frame.OperationId != operation ||
                frame.InstanceId != instance.InstanceId || frame.Parent != parent) throw Invalid();
            using var origin = await MaintenanceStartupOrigin.ReceiveSuccessorAsync(pipe, producer, parent,
                frame.JobHandle, deadline.Token);
            await pipe.WriteAsync(new byte[] { 2 }, deadline.Token);
            await pipe.FlushAsync(deadline.Token);
            var commit = new byte[1];
            await pipe.ReadExactlyAsync(commit, deadline.Token);
            if (commit[0] != 3) throw Invalid();
            await producer.WaitForExitAsync(deadline.Token);
            origin.RequireReleasedSuccessor();
            pipe.Dispose(); producer.Dispose();
            return await ApplicationHost.RunAsync(instance.RuntimeRoot, null, maintenanceOrigin: origin, resumeWindow: frame.Window);
        }
        catch (Exception error) when (error is IOException or ArgumentException or JsonException or
            System.ComponentModel.Win32Exception or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException)
        {
            Console.Error.WriteLine("host_restart_startup_rejected");
            return 1;
        }
    }

    private static IOException Invalid() => new("host_restart_handoff_invalid");
}
