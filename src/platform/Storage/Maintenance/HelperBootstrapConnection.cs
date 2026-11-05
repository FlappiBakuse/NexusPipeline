using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Maintenance;

[SupportedOSPlatform("windows")]
internal sealed class HelperBootstrapConnection : IDisposable
{
    private readonly NativeClientBinding _parent;
    private readonly NamedPipeClientStream _pipe;
    internal MaintenanceRegisteredActor Actor { get; }
    internal MaintenanceStartupOrigin Origin { get; }

    private HelperBootstrapConnection(MaintenanceRegisteredActor actor, NativeClientBinding parent, NamedPipeClientStream pipe,
        MaintenanceStartupOrigin origin)
    { Actor = actor; _parent = parent; _pipe = pipe; Origin = origin; }

    internal static async Task<HelperBootstrapConnection> ConnectAsync(MaintenanceBootstrapPayload bootstrap,
        string pipeName, string jobName, CancellationToken cancellation)
    {
        if (pipeName is not { Length: >= 1 and <= 180 } || pipeName.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))) throw Invalid();
        var producers = HelperPeerReader.Read(bootstrap);
        var actor = MaintenanceRegisteredActor.BindCurrent(bootstrap, producers.WriterId, jobName);
        NativeClientBinding? parent = null; NamedPipeClientStream? pipe = null; MaintenanceStartupOrigin? origin = null;
        try
        {
            parent = new(producers.Parent);
            pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(cancellation).ConfigureAwait(false);
            RequireParent(pipe, parent, producers.Parent.ProcessId);
            var challenge = MaintenanceHelperFrames.ReadChallenge(await MaintenanceStartupFrames.ReadAsync(pipe, cancellation).ConfigureAwait(false));
            if (challenge.Binding != MaintenanceHelperFrames.Binding(bootstrap.Inputs) || challenge.WriterId != actor.Writer.WriterId ||
                challenge.JobName != actor.JobName) throw Invalid();
            actor.RequireBound(); RequireParent(pipe, parent, producers.Parent.ProcessId);
            origin = await MaintenanceStartupOrigin.ReceiveSuccessorAsync(pipe, parent, producers.Parent, challenge.JobHandle, cancellation).ConfigureAwait(false);
            await MaintenanceStartupFrames.WriteAsync(pipe, MaintenanceHelperFrames.Encode(MaintenanceHelperFrames.Ready(challenge)), cancellation).ConfigureAwait(false);
            var acknowledgement = new byte[1]; await pipe.ReadExactlyAsync(acknowledgement, cancellation).ConfigureAwait(false);
            actor.RequireBound();
            if (acknowledgement[0] != 1) throw Invalid();
            return new(actor, parent, pipe, origin);
        }
        catch
        {
            try { pipe?.Dispose(); } finally { try { parent?.Dispose(); } finally { try { origin?.Dispose(); } finally { actor.Dispose(); } } }
            throw;
        }
    }

    internal async Task AwaitParentAsync(CancellationToken cancellation)
    {
        await _parent.WaitForExitAsync(cancellation).ConfigureAwait(false);
        // The verified parent's image lease must close before its payload tree can move.
        _parent.Dispose();
    }

    private static void RequireParent(NamedPipeClientStream pipe, NativeClientBinding parent, int processId)
    {
        if (!parent.IsLive || !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var actual) || actual != processId) throw Invalid();
    }

    public void Dispose()
    {
        try { _pipe.Dispose(); } finally { try { _parent.Dispose(); } finally { try { Origin.Dispose(); } finally { Actor.Dispose(); } } }
    }
    private static IOException Invalid() => new("maintenance_helper_bootstrap_unbound");

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out int processId);
}
