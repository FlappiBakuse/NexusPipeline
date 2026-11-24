using NexusPipeline.Core.Maintenance;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Storage;
using System.Runtime.Versioning;
using System.Text.Json;

namespace NexusPipeline.Host.Desktop;

internal sealed class DesktopLaunch : IDisposable
{
    private readonly ManualResetEventSlim _release = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task<OwnedWorkerIdentity> _launch;
    private bool _disposed;
    private bool _started;
    internal OwnedWorkerIdentity Identity { get; }

    internal DesktopLaunch(OwnedWorkerJob job, string executable, IReadOnlyList<string> arguments,
        string directory, Guid instanceId, ProcessIdentityLease image)
    {
        var registered = new TaskCompletionSource<OwnedWorkerIdentity>(TaskCreationOptions.RunContinuationsAsynchronously);
        _launch = Task.Factory.StartNew(() =>
        {
            try
            {
                return job.StartDesktop(executable, arguments, directory, instanceId, Guid.NewGuid(), identity =>
                {
                    registered.TrySetResult(identity);
                    _release.Wait(_stop.Token);
                }, image);
            }
            catch (Exception error) { registered.TrySetException(error); throw; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try { Identity = registered.Task.GetAwaiter().GetResult(); }
        catch { Dispose(); throw; }
    }

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) throw new InvalidOperationException("desktop_already_started");
        _started = true;
        _release.Set();
        _launch.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        try { _launch.GetAwaiter().GetResult(); }
        // Construction or Start owns startup failures; cleanup must still release the suspended launch and its handles.
        catch (Exception) { }
        finally { _release.Dispose(); _stop.Dispose(); }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class OwnedDesktop : IDisposable
{
    private readonly OwnedWorkerJob _job = new();
    private readonly ProcessStartObserver _observer;
    private readonly OwnedWorkerIdentity _root;
    private readonly DesktopLaunch _launch;
    private DesktopControl? _control;
    internal NativeClientBinding Binding { get; }

    internal OwnedDesktop(RuntimeInstance instance, string payloadRoot, DesktopResponseSigner signer,
        UpdateWindowIntent intent, bool minimizeToTray, StartupChannel channel, string locale)
    {
        _observer = _job.ObserveProcessStarts(_ => { });
        DesktopLaunch? launch = null;
        ProcessIdentityLease? image = null;
        NativeClientBinding? binding = null;
        try
        {
            var executable = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(payloadRoot, ApplicationTree.DesktopEntry));
            var profile = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, RuntimeInstance.DesktopProfileDirectory));
            Directory.CreateDirectory(profile);
            foreach (var name in new[] { "session", "dumps", "logs" })
                Directory.CreateDirectory(PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(profile, name)));
            var bootstrap = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
            {
                startupPipe = channel.Name, nonce = channel.Nonce, locale,
                instanceId = instance.InstanceId, verificationKey = signer.VerificationKeyBase64,
                profilePath = profile, visible = intent.Visible, route = intent.Route, minimizeToTray,
                mode = intent.Mode == UpdateWindowMode.Desktop ? "desktop" : "browser"
            }));
            // Native Chromium diagnostics may be emitted before Electron configures its application paths.
            var log = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(profile, "logs", "chromium.log"));
            image = ProcessIdentityLease.HoldImage(executable);
            launch = new(_job, executable, ["--js-flags=--max-semi-space-size=1", "--enable-logging=file",
                "--log-file=" + log, "--nexus-bootstrap=" + bootstrap], profile, instance.InstanceId, image);
            _launch = launch;
            _root = launch.Identity;
            binding = new(new ProcessIdentity(_root.ProcessId, _root.CreatedAtUtc, _root.ImagePath,
                _root.ImageSha256, _root.UserSid, _root.IsElevated), image);
            Binding = binding;
            channel.Bind(_root.ProcessId);
        }
        catch { launch?.Dispose(); binding?.Dispose(); image?.Dispose(); _observer.Dispose(); _job.Dispose(); throw; }
    }

    internal void Start() => _launch.Start();

    internal void BindControl(DesktopControl control)
    {
        if (_control is not null) throw new InvalidOperationException("desktop_control_already_bound");
        _control = control;
    }

    internal async Task ExitForMaintenanceAsync(PreparedDesktopExit prepared, CancellationToken cancellation)
    {
        var control = _control ?? throw new IOException("desktop_control_unavailable");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        control.PrepareMaintenanceExit(prepared);
        try { await AwaitFamilyAsync(deadline.Token); }
        finally { control.ClearMaintenanceExit(prepared); }
        // The old image lease must outlive every owned member, but must not block payload replacement.
        Binding.Dispose();
    }

    internal async Task AwaitFamilyAsync(CancellationToken cancellation)
    {
        while (await _job.AwaitRootAsync(_root, TimeSpan.FromMilliseconds(100), cancellation) is null) { }
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var accounting = _job.GetAccounting();
            if (accounting.ActiveProcesses == 0)
            {
                _ = _job.RequireStoppedFamily();
                return;
            }
            await Task.Delay(50, cancellation);
        }
    }

    public void Dispose()
    {
        _launch.Dispose();
        Binding.Dispose();
        _observer.Dispose();
        _job.Dispose();
    }
}
