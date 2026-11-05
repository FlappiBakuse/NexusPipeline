using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Maintenance;

[SupportedOSPlatform("windows")]
internal sealed class HelperReadyServer : IDisposable
{
    private readonly PreparedUpdateHandoff _handoff;
    private readonly InstanceLease _lease;
    private readonly MaintenanceBootstrapPayload _bootstrap;
    private readonly NamedPipeServerStream _pipe;
    private NativeClientBinding? _helper;
    private int _used;
    internal string PipeName { get; } = "NexusMaintenanceHelper-" + Guid.NewGuid().ToString("N");
    internal bool IsAcknowledged { get; private set; }

    internal HelperReadyServer(PreparedUpdateHandoff handoff, InstanceLease lease,
        MaintenanceBootstrapPayload bootstrap)
    {
        _handoff = handoff; _lease = lease; _bootstrap = bootstrap; RequireOldHost();
        _pipe = new(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
    }

    internal async Task WaitReadyAsync(MaintenanceStartupJob actualJob, OwnedWorkerIdentity actualRoot,
        Guid writerId, CancellationToken cancellation)
    {
        if (Interlocked.Exchange(ref _used, 1) != 0) throw Invalid();
        RequireOldHost();
        var helper = new ProcessIdentity(actualRoot.ProcessId, actualRoot.CreatedAtUtc, actualRoot.ImagePath,
            actualRoot.ImageSha256, actualRoot.UserSid, actualRoot.IsElevated);
        _bootstrap.RequireHelper(helper); RequireFamily(actualJob, helper);
        var peer = new NativeClientBinding(helper);
        try
        {
            using var offeredJob = actualJob.RetainSuccessorJob(helper);
            var challenge = MaintenanceHelperFrames.Challenge(_bootstrap.Inputs, writerId, actualJob.Name, offeredJob.ExportValue);
            await _pipe.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
            RequirePeer(peer, helper);
            await MaintenanceStartupFrames.WriteAsync(_pipe, MaintenanceHelperFrames.Encode(challenge), cancellation).ConfigureAwait(false);
            await MaintenanceStartupOrigin.AcknowledgeSuccessorAsync(_pipe, actualJob, offeredJob, peer, helper, cancellation).ConfigureAwait(false);
            var reply = MaintenanceHelperFrames.ReadReady(await MaintenanceStartupFrames.ReadAsync(_pipe, cancellation).ConfigureAwait(false));
            if (reply != MaintenanceHelperFrames.Ready(challenge)) throw Invalid();
            RequireOldHost(); RequirePeer(peer, helper); RequireFamily(actualJob, helper);
            await _pipe.WriteAsync(new byte[] { 1 }, cancellation).ConfigureAwait(false);
            await _pipe.FlushAsync(cancellation).ConfigureAwait(false);
            _helper = peer; IsAcknowledged = true;
        }
        catch { peer.Dispose(); throw; }
    }

    private void RequireOldHost()
    {
        _lease.VerifyHeld(_bootstrap.Inputs.Instance); _bootstrap.RequireCurrent();
        if (!_handoff.Root.IsActive || _handoff.PlanReference != _bootstrap.Inputs.Operation.Plan ||
            _handoff.HandoffId != _bootstrap.Inputs.Operation.HandoffId) throw Invalid();
    }
    private static void RequireFamily(MaintenanceStartupJob job, ProcessIdentity helper)
    {
        var family = job.CapturePreparedFamily();
        if (family is not { TotalProcesses: 1, ActiveProcesses: 1 } || !family.LivingMembers.SequenceEqual([helper])) throw Invalid();
    }
    private void RequirePeer(NativeClientBinding peer, ProcessIdentity helper)
    {
        if (!peer.IsLive || !GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var processId) || processId != helper.ProcessId) throw Invalid();
    }

    public void Dispose() { try { _pipe.Dispose(); } finally { _helper?.Dispose(); } }
    private static IOException Invalid() => new("maintenance_helper_ready_peer_unbound");

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out int processId);
}
