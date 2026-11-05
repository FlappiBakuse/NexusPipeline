using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Maintenance;

[SupportedOSPlatform("windows")]
internal sealed class MaintenanceStartupClient : IDisposable
{
    private readonly InstanceLease _lease;
    private readonly MaintenanceStartupBinding _binding;
    private readonly NativeClientBinding _helper;
    private readonly ProcessIdentity _helperIdentity;
    private MaintenanceStartupOrigin? _origin;
    private MaintenanceJobQuery? _helperJob;
    private bool _prepared;
    private bool _helperStopped;
    private MaintenanceStartupChallenge? _challenge;
    private MaintenanceStartupReply? _reply;
    internal MaintenanceStartupOrigin Origin => _prepared ? _origin ?? throw Invalid() : throw Invalid();
    private readonly NamedPipeClientStream _pipe;
    private int _used;

    internal MaintenanceStartupClient(InstanceLease lease, MaintenanceStartupBinding binding, ProcessIdentity trustedHelper, string pipeName)
    {
        MaintenanceStartupFrames.Require(binding);
        lease.VerifyHeld(lease.Instance);
        if (lease.Instance.InstanceId != binding.InstanceId || pipeName is not { Length: >= 1 and <= 180 } ||
            pipeName.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')) ||
            trustedHelper.IsElevated != ProcessIdentityReader.Read(Environment.ProcessId).IsElevated)
            throw Invalid();
        _lease = lease; _binding = binding; _helperIdentity = trustedHelper;
        _helper = new(trustedHelper);
        try { _pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); }
        catch { _helper.Dispose(); throw; }
    }

    internal async Task SendPreparedAsync(Func<MaintenanceStartupChallenge, CancellationToken, Task<BlobReference>> prepareReceipt,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(prepareReceipt);
        if (Interlocked.Exchange(ref _used, 1) != 0) throw Invalid();
        _lease.VerifyHeld(_lease.Instance);
        await _pipe.ConnectAsync(cancellation).ConfigureAwait(false);
        RequireHelper();
        var challenge = MaintenanceStartupFrames.ReadChallenge(await MaintenanceStartupFrames.ReadAsync(_pipe, cancellation).ConfigureAwait(false));
        if (challenge.Binding != _binding) throw Invalid();
        _lease.VerifyHeld(_lease.Instance); RequireHelper();
        _origin = await MaintenanceStartupOrigin.ReceiveSuccessorAsync(_pipe, _helper, _helperIdentity,
            challenge.CandidateJobHandle, cancellation).ConfigureAwait(false);
        _helperJob = MaintenanceJobQuery.Receive(_helper, _helperIdentity, challenge.HelperJobHandle);
        _helperJob.RequireMember(_helperIdentity);
        using (var current = JobNative.OpenProcess(0x101000, false, Environment.ProcessId)) _helperJob.RequireExcluded(current);
        if (_helperJob.Flags != 0x2800 || _helperJob.Accounting is not { TotalProcesses: 1, ActiveProcesses: 1 } ||
            !_helperJob.ActiveProcessIds.SequenceEqual([_helperIdentity.ProcessId])) throw Invalid();
        var echo = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(echo, _helperJob.ExportValue);
        await _pipe.WriteAsync(echo, cancellation).ConfigureAwait(false);
        await _pipe.FlushAsync(cancellation).ConfigureAwait(false);
        var retained = new byte[1]; await _pipe.ReadExactlyAsync(retained, cancellation).ConfigureAwait(false);
        RequireHelper();
        if (retained[0] != 1) throw Invalid();
        var receipt = await prepareReceipt(challenge, cancellation).ConfigureAwait(false);
        _lease.VerifyHeld(_lease.Instance); RequireHelper();
        var reply = new MaintenanceStartupReply(MaintenanceStartupFrames.Format, 1, "prepared", _binding,
            challenge.Nonce, _lease.StartupGeneration, receipt);
        await MaintenanceStartupFrames.WriteAsync(_pipe, MaintenanceStartupFrames.Encode(reply), cancellation).ConfigureAwait(false);
        var acknowledgement = new byte[1]; await _pipe.ReadExactlyAsync(acknowledgement, cancellation).ConfigureAwait(false);
        _lease.VerifyHeld(_lease.Instance);
        if (acknowledgement[0] != 1) throw Invalid();
        _challenge = challenge; _reply = reply;
        _prepared = true;
    }

    internal async Task AwaitHelperAsync(CancellationToken cancellation)
    {
        if (!_prepared || _helperJob is null) throw Invalid();
        await _helper.WaitForExitAsync(cancellation).ConfigureAwait(false);
        // Keep the original kernel object alive: a missing named Job cannot prove that its family stopped.
        if (_helperJob.Accounting is not { TotalProcesses: 1, ActiveProcesses: 0 } || _helperJob.ActiveProcessIds.Count != 0)
            throw Invalid();
        Origin.RequireReleasedSuccessor();
        _lease.VerifyHeld(_lease.Instance);
        _helperStopped = true;
    }

    internal (MaintenanceStartupChallenge Challenge, MaintenanceStartupReply Reply) RequireCompleted(InstanceLease lease)
    {
        if (!ReferenceEquals(lease, _lease) || !_helperStopped || _challenge is null || _reply is null || _helperJob is null ||
            _helperJob.Accounting is not { TotalProcesses: 1, ActiveProcesses: 0 } || _helperJob.ActiveProcessIds.Count != 0)
            throw Invalid();
        Origin.RequireReleasedSuccessor(); lease.VerifyHeld(lease.Instance);
        return (_challenge, _reply);
    }

    internal MaintenanceStartupOrigin DetachCompletedOrigin(InstanceLease lease)
    {
        _ = RequireCompleted(lease);
        return Interlocked.Exchange(ref _origin, null) ?? throw Invalid();
    }

    private void RequireHelper()
    {
        if (!_helper.IsLive || !GetNamedPipeServerProcessId(_pipe.SafePipeHandle, out var processId) || processId != _helperIdentity.ProcessId)
            throw Invalid();
    }

    public void Dispose()
    {
        try { _pipe.Dispose(); }
        finally { try { _helper.Dispose(); } finally { try { _origin?.Dispose(); } finally { _helperJob?.Dispose(); } } }
    }
    private static IOException Invalid() => new("maintenance_startup_helper_unbound");

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out int processId);
}
