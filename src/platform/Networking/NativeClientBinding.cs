using System.ComponentModel;
using System.Net;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Networking;

[SupportedOSPlatform("windows")]
public sealed class NativeClientBinding : IDisposable
{
    private readonly ProcessIdentity _registered;
    private readonly ProcessIdentityLease _image;
    private readonly SafeKernelHandle _process;
    private readonly object _gate = new();
    private bool _disposed;

    public NativeClientBinding(int trustedProcessId) : this(trustedProcessId, null) { }

    public NativeClientBinding(ProcessIdentity trustedIdentity) : this(trustedIdentity.ProcessId, trustedIdentity) { }

    // A successful registration takes ownership; failed registration leaves the caller's protection intact.
    internal NativeClientBinding(ProcessIdentity trustedIdentity, ProcessIdentityLease protectedImage)
        : this(trustedIdentity.ProcessId, trustedIdentity, protectedImage) { }

    private NativeClientBinding(int trustedProcessId, ProcessIdentity? trustedIdentity, ProcessIdentityLease? protectedImage = null)
    {
        if (trustedProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(trustedIdentity));
        _process = JobNative.OpenProcess(0x101000, false, trustedProcessId);
        try
        {
            if (_process.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (!IsRunning()) throw new IOException("native_client_not_running");
            var metadata = ProcessIdentityReader.ReadMetadata(_process, trustedProcessId);
            using var host = JobNative.OpenProcess(0x1000, false, Environment.ProcessId);
            if (metadata.UserSid != ProcessIdentityReader.ReadMetadata(host, Environment.ProcessId).UserSid)
                throw new IOException("native_client_user_mismatch");
            var image = protectedImage ?? ProcessIdentityLease.HoldImage(metadata.ImagePath);
            try
            {
                _registered = image.Read(_process, trustedProcessId);
                if (trustedIdentity is not null && _registered != trustedIdentity)
                    throw new IOException("native_client_registration_identity_mismatch");
                if (ProcessIdentityMetadata.From(_registered) != metadata || image.Read(_process, trustedProcessId) != _registered || !IsRunning())
                    throw new IOException("native_client_changed_during_registration");
                _image = image;
            }
            catch { if (protectedImage is null) image.Dispose(); throw; }
        }
        catch { _process.Dispose(); throw; }
    }

    public bool Matches(IPEndPoint client, IPEndPoint server)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            try
            {
                var owner = LoopbackTcpPeer.Observe(client, server);
                return owner.Status == TcpOwnerStatus.Matched && owner.ProcessId == _registered.ProcessId && IsRunning() &&
                    MatchesPinnedIdentity(owner.ProcessId.Value) && IsRunning();
            }
            catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }
        }
    }

    public bool IsLive
    {
        get
        {
            lock (_gate)
            {
                if (_disposed) return false;
                try { return IsRunning() && MatchesPinnedIdentity(_registered.ProcessId) && IsRunning(); }
                catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException) { return false; }
            }
        }
    }

    internal bool IsRegistered(ProcessIdentity expected)
    {
        lock (_gate) return !_disposed && _registered == expected && IsLive;
    }

    public T UseVerifiedLifetime<T>(Func<T> effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        lock (_gate)
        {
            if (!IsLive) throw new UnauthorizedAccessException("native_client_lifetime_unavailable");
            return effect();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _image.Dispose();
            _process.Dispose();
        }
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var retained = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _process.DangerousAddRef(ref retained);
        }
        try
        {
            // The registered kernel object remains authoritative while a departing PID disappears from process enumeration.
            using var wait = new RegisteredProcessWait(_process.DangerousGetHandle());
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = ThreadPool.RegisterWaitForSingleObject(wait,
                (_, _) => completed.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
            try
            {
                using var cancellation = cancellationToken.Register(() => completed.TrySetCanceled(cancellationToken));
                await completed.Task.ConfigureAwait(false);
            }
            finally { registration.Unregister(null); }
        }
        finally { if (retained) _process.DangerousRelease(); }
    }

    private sealed class RegisteredProcessWait : WaitHandle
    {
        internal RegisteredProcessWait(nint handle) => SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: false);
    }

    private bool IsRunning() => JobNative.WaitForSingleObject(_process, 0) == 258;

    private bool MatchesPinnedIdentity(int processId) => _image.Read(_process, processId) == _registered;
}
