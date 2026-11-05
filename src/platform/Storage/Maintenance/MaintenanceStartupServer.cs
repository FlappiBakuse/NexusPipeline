using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Maintenance;

[SupportedOSPlatform("windows")]
internal sealed class MaintenanceStartupServer : IDisposable
{
    private readonly MaintenanceStartupBinding _binding;
    private readonly NamedPipeServerStream _pipe;
    private MaintenanceStartupChallenge? _challenge;
    internal MaintenanceStartupChallenge Challenge => _challenge ?? throw Invalid();
    private NativeClientBinding? _peer;
    private ProcessIdentity? _host;
    private MaintenanceStartupReply? _reply;
    private int _used;
    internal string PipeName { get; } = "NexusMaintenanceStartup-" + Guid.NewGuid().ToString("N");

    internal MaintenanceStartupServer(MaintenanceStartupBinding binding)
    {
        MaintenanceStartupFrames.Require(binding); _binding = binding;
        _pipe = new(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
    }

    internal async Task<MaintenanceStartupReply> ReceivePreparedAsync(MaintenanceStartupJob actualJob,
        ProcessIdentity actualHost, MaintenanceRegisteredActor actor, CancellationToken cancellation, MaintenanceStartupInputs? restored = null)
    {
        if (Interlocked.Exchange(ref _used, 1) != 0) throw Invalid();
        RequireMember(actualJob, actualHost);
        var peer = new NativeClientBinding(actualHost);
        try
        {
            await AwaitConnectionAsync(actualJob, actualHost, cancellation).ConfigureAwait(false);
            RequirePeer(peer, actualHost);
            if (actualHost.IsElevated != ProcessIdentityReader.Read(Environment.ProcessId).IsElevated) throw Invalid();
            using var candidateJob = actualJob.RetainSuccessorJob(actualHost);
            using var helperJob = actor.RetainJobQuery(restored);
            _challenge = MaintenanceStartupFrames.Challenge(_binding, candidateJob.ExportValue, helperJob.ExportValue);
            await MaintenanceStartupFrames.WriteAsync(_pipe, MaintenanceStartupFrames.Encode(_challenge), cancellation).ConfigureAwait(false);
            await MaintenanceStartupOrigin.AcknowledgeSuccessorAsync(_pipe, actualJob, candidateJob, peer, actualHost, cancellation).ConfigureAwait(false);
            var echo = new byte[8]; await _pipe.ReadExactlyAsync(echo, cancellation).ConfigureAwait(false);
            using var retainedHelper = MaintenanceJobQuery.Receive(peer, actualHost, BinaryPrimitives.ReadInt64LittleEndian(echo));
            helperJob.RequireSameObject(retainedHelper);
            if (restored is null) actor.RequireBound(); else actor.RequireRestoredBound(restored);
            RequirePeer(peer, actualHost);
            await _pipe.WriteAsync(new byte[] { 1 }, cancellation).ConfigureAwait(false);
            await _pipe.FlushAsync(cancellation).ConfigureAwait(false);
            var reply = MaintenanceStartupFrames.ReadReply(await MaintenanceStartupFrames.ReadAsync(_pipe, cancellation).ConfigureAwait(false));
            if (reply.Binding != _binding || reply.Nonce != _challenge.Nonce) throw Invalid();
            RequirePeer(peer, actualHost); RequireMember(actualJob, actualHost);
            _host = actualHost; _reply = reply; _peer = peer;
            return reply;
        }
        catch { peer.Dispose(); throw; }
    }

    private async Task AwaitConnectionAsync(MaintenanceStartupJob job, ProcessIdentity host, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var pending = _pipe.WaitForConnectionAsync(deadline.Token);
        var root = new OwnedWorkerIdentity(host.ProcessId, host.CreatedAtUtc, host.ImagePath, host.ImageSha256,
            host.UserSid, host.IsElevated, _binding.InstanceId, _binding.HandoffId);
        while (!pending.IsCompleted)
        {
            if (await job.AwaitRootAsync(root, TimeSpan.FromMilliseconds(100)).ConfigureAwait(false) is null) continue;
            deadline.Cancel();
            try { await pending.ConfigureAwait(false); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            throw new IOException("maintenance_candidate_exited_before_connection");
        }
        await pending.ConfigureAwait(false);
    }

    internal MaintenanceStartupInventory VerifyCurrentPeer(MaintenanceStartupJob actualJob, MaintenanceStartupReply expected)
    {
        if (_host is null || _peer is null || _reply is null || !ReferenceEquals(_reply, expected)) throw Invalid();
        RequirePeer(_peer, _host);
        return RequireMember(actualJob, _host);
    }

    internal async Task ReleasePreparedAsync(MaintenanceStartupJob actualJob, MaintenanceStartupReply expected,
        StartupReceiptLease verifiedReceipt, Sqlite.StateDatabase database, CancellationToken cancellation)
    {
        _ = VerifyCurrentPeer(actualJob, expected);
        verifiedReceipt.RequireCurrent(database, _binding);
        if (verifiedReceipt.Reference != expected.Receipt || verifiedReceipt.StartupGeneration != expected.StartupGeneration ||
            verifiedReceipt.Host != _host || verifiedReceipt.JobName != actualJob.Name) throw Invalid();
        actualJob.ReleasePreparedRoot(_host!);
        await _pipe.WriteAsync(new byte[] { 1 }, cancellation).ConfigureAwait(false);
        await _pipe.FlushAsync(cancellation).ConfigureAwait(false);
    }

    private void RequirePeer(NativeClientBinding peer, ProcessIdentity host)
    {
        if (!peer.IsLive || !GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var processId) || processId != host.ProcessId) throw Invalid();
    }

    private static MaintenanceStartupInventory RequireMember(MaintenanceStartupJob job, ProcessIdentity host)
    {
        var family = job.CapturePreparedFamily();
        if (family is not { TotalProcesses: 1, ActiveProcesses: 1 } || !family.LivingMembers.SequenceEqual([host])) throw Invalid();
        return family;
    }

    public void Dispose() { try { _pipe.Dispose(); } finally { _peer?.Dispose(); } }
    private static IOException Invalid() => new("maintenance_startup_host_unbound");

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out int processId);
}
