using System.ComponentModel;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Platform.Networking;

[SupportedOSPlatform("windows")]
public sealed class NativeClientBinding : IDisposable
{
    private readonly ProcessIdentity _registered;
    private readonly ProcessIdentityMetadata _metadata;
    private readonly PhysicalObjectIdentity _imageIdentity;
    private readonly FileStream _image;
    private readonly SafeKernelHandle _process;
    private readonly object _gate = new();
    private bool _disposed;

    public NativeClientBinding(int trustedProcessId) : this(ProcessIdentityReader.Read(trustedProcessId)) { }

    public NativeClientBinding(ProcessIdentity trustedIdentity)
    {
        var trustedProcessId = trustedIdentity.ProcessId;
        if (trustedProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(trustedIdentity));
        _process = JobNative.OpenProcess(0x101000, false, trustedProcessId);
        try
        {
            if (_process.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (!IsRunning()) throw new IOException("native_client_not_running");
            _registered = ProcessIdentityReader.Read(_process, trustedProcessId);
            if (_registered != trustedIdentity) throw new IOException("native_client_registration_identity_mismatch");
            _metadata = ProcessIdentityMetadata.From(_registered);
            var host = ProcessIdentityReader.Read(Environment.ProcessId);
            if (_registered.UserSid != host.UserSid) throw new IOException("native_client_user_mismatch");
            var image = new FileStream(_registered.ImagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                var imageIdentity = PhysicalObjectIdentity.Read(image.SafeFileHandle);
                if (Convert.ToHexStringLower(SHA256.HashData(image)) != _registered.ImageSha256 ||
                    PhysicalObjectIdentity.Read(_registered.ImagePath) != imageIdentity ||
                    ProcessIdentityReader.ReadMetadata(_process, trustedProcessId) != _metadata || !IsRunning())
                    throw new IOException("native_client_changed_during_registration");
                _imageIdentity = imageIdentity;
                _image = image;
            }
            catch { image.Dispose(); throw; }
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

    private bool MatchesPinnedIdentity(int processId) =>
        ProcessIdentityReader.ReadMetadata(_process, processId) == _metadata &&
        PhysicalObjectIdentity.Read(_registered.ImagePath) == _imageIdentity;
}
