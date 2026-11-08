using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Host.Desktop;

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
            var profile = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "desktop-profile"));
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
