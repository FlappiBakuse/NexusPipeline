using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Maintenance;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Settings;
using System.ComponentModel;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NexusPipeline.Host.Lifecycle;

[SupportedOSPlatform("windows")]
internal sealed class UpdateWindowSession : IAsyncDisposable
{
    private readonly HostUpdateService _updates;
    private readonly HostMaintenanceController _maintenance;
    private readonly HostUpdateChannel _channel;
    private readonly UpdateWindowIntent _window;
    private readonly string _subject = "update-window:" + Guid.NewGuid().ToString("N");
    private readonly object _gate = new();
    private UpdateWindowStatus _status;
    private UpdateFeedIdentity? _feed;
    private Guid? _operation;
    private Task _work = Task.CompletedTask;
    private CancellationTokenSource? _action;
    private bool _handedOff;

    internal UpdateWindowSession(HostUpdateService updates, HostMaintenanceController maintenance,
        HostUpdateChannel channel, UpdateWindowIntent window)
    {
        _updates = updates; _maintenance = maintenance; _channel = channel; _window = window;
        _status = new(Assembly.GetExecutingAssembly().GetName().Version!.ToString(3), null, UpdateWindowPhase.Idle);
        if (!updates.HasReleaseTrust)
        {
            _status = _status with { Phase = UpdateWindowPhase.Blocked, Reason = "maintenance_release_trust_unavailable" };
            return;
        }
        try
        {
            if (updates.ReadCached(channel) is { } feed) AcceptFeed(feed);
        }
        catch (Exception error) when (UpdateWindowPipe.IsFailure(error))
        {
            _status = _status with { Reason = UpdateWindowPipe.Reason(error) };
        }
    }

    internal async Task<UpdateWindowStatus> ExecuteAsync(UpdateWindowCommand command, NativeClientBinding peer,
        CancellationToken cancellation)
    {
        try
        {
            switch (command)
            {
                case UpdateWindowCommand.Check when _work.IsCompleted && Status.Phase is UpdateWindowPhase.Idle or UpdateWindowPhase.Current or UpdateWindowPhase.Available:
                    Begin(UpdateWindowPhase.Checking, async token => AcceptFeed(await _updates.RefreshAsync(_subject, _channel, token)), cancellation);
                    break;
                case UpdateWindowCommand.Download when _work.IsCompleted && Status.Phase == UpdateWindowPhase.Available:
                    var request = new HostUpdateRequest(Guid.NewGuid(), _subject, _feed!, Status.Available!, HostUpdateTiming.Now, _window);
                    _operation = request.OperationId;
                    Begin(UpdateWindowPhase.Downloading, token => DownloadAsync(request, token), cancellation);
                    break;
                case UpdateWindowCommand.Cancel:
                    CancelDownload();
                    break;
                case UpdateWindowCommand.Apply when _work.IsCompleted && Status.Phase == UpdateWindowPhase.Ready:
                    var accepted = await _maintenance.StartAsync(_operation!.Value, _subject, cancellation, peer);
                    if (accepted.State != HostUpdateState.Starting) throw new IOException("maintenance_window_apply_rejected");
                    _handedOff = true;
                    SetStatus(status => status with { Phase = UpdateWindowPhase.HandedOff, Reason = null });
                    break;
                case UpdateWindowCommand.Close:
                    await DisposeAsync();
                    SetStatus(status => status with { Phase = UpdateWindowPhase.Closed });
                    break;
            }
        }
        catch (Exception error) when (UpdateWindowPipe.IsFailure(error))
        {
            SetStatus(status => status with { Reason = UpdateWindowPipe.Reason(error) });
        }
        return Status;
    }

    private void CancelDownload()
    {
        lock (_gate)
        {
            if (_status.Phase != UpdateWindowPhase.Downloading) return;
            _status = _status with { Phase = UpdateWindowPhase.Cancelling };
        }
        _action!.Cancel();
    }

    private void Begin(UpdateWindowPhase phase, Func<CancellationToken, Task> action, CancellationToken cancellation)
    {
        _action?.Dispose();
        _action = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        SetStatus(status => status with { Phase = phase, Received = 0, Total = 0, Reason = null });
        var token = _action.Token;
        _work = Task.Run(async () =>
        {
            try { await action(token); }
            catch (Exception error) when (UpdateWindowPipe.IsFailure(error))
            {
                var cancelled = error is OperationCanceledException && token.IsCancellationRequested;
                SetStatus(status => status with
                {
                    Phase = error is AggregateException ? UpdateWindowPhase.Blocked : AvailablePhase(status),
                    Received = 0, Total = 0, Reason = cancelled ? null : UpdateWindowPipe.Reason(error)
                });
            }
        });
    }

    private async Task DownloadAsync(HostUpdateRequest request, CancellationToken cancellation)
    {
        var result = await _updates.PrepareAsync(request, cancellation, (received, total) =>
            SetStatus(status => status.Phase == UpdateWindowPhase.Downloading
                ? status with { Received = received, Total = total } : status));
        if (result.State != HostUpdateState.Pending) throw new IOException("maintenance_window_prepare_rejected");
        lock (_gate)
        {
            if (_status.Phase == UpdateWindowPhase.Downloading && !cancellation.IsCancellationRequested)
            {
                _status = _status with { Phase = UpdateWindowPhase.Ready };
                return;
            }
        }
        CancelPending();
        cancellation.ThrowIfCancellationRequested();
        SetStatus(status => status with { Phase = AvailablePhase(status), Received = 0, Total = 0 });
    }

    private void AcceptFeed(HostUpdateFeed feed)
    {
        var current = Status.Current;
        var latest = feed.Releases.Where(release => AutomaticUpdatePolicy.CompareVersions(release.HostVersion, current) > 0 &&
                AutomaticUpdatePolicy.CompareVersions(release.MinReaderVersion, current) <= 0)
            .OrderByDescending(release => release.HostVersion, Comparer<string>.Create(AutomaticUpdatePolicy.CompareVersions))
            .FirstOrDefault();
        _feed = feed.Identity;
        SetStatus(status => status with { Available = latest?.HostVersion,
            Phase = latest is null ? UpdateWindowPhase.Current : UpdateWindowPhase.Available, Reason = null });
    }

    private void CancelPending()
    {
        if (_operation is { } id && _maintenance.Read(id, _subject)?.State == HostUpdateState.Pending)
            _ = _maintenance.Cancel(id, _subject);
        _operation = null;
    }

    private UpdateWindowStatus Status { get { lock (_gate) return _status; } }
    private void SetStatus(Func<UpdateWindowStatus, UpdateWindowStatus> update)
    { lock (_gate) _status = update(_status); }
    private static UpdateWindowPhase AvailablePhase(UpdateWindowStatus status) =>
        status.Available is null ? UpdateWindowPhase.Idle : UpdateWindowPhase.Available;

    public async ValueTask DisposeAsync()
    {
        _action?.Cancel();
        await _work;
        _action?.Dispose(); _action = null;
        if (!_handedOff) CancelPending();
    }
}

[SupportedOSPlatform("windows")]
internal static class UpdateWindowHost
{
    internal static bool IsDefault(string root, RuntimeInstance instance) =>
        StringComparer.OrdinalIgnoreCase.Equals(instance.RuntimeRoot, Path.Combine(root, "runtime")) &&
        StringComparer.OrdinalIgnoreCase.Equals(PhysicalPathPolicy.RuntimeProcessPath(), Path.Combine(root, ApplicationTree.HostEntry));

    internal static async Task ServeAsync(string root, HostUpdateService updates, HostMaintenanceController maintenance,
        HostSettingsService settings, UpdateWindowIntent window, CancellationToken cancellation, int? caller = null)
    {
        NamedPipeServerStream pipe;
        try { pipe = UpdateWindowPipe.Listen(root); }
        catch (IOException error) when (caller is null)
        { Console.Error.WriteLine(UpdateWindowPipe.Reason(error)); return; }
        using var listener = pipe;
        do
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                if (caller is not null) deadline.CancelAfter(TimeSpan.FromSeconds(30));
                await pipe.WaitForConnectionAsync(deadline.Token);
                await HandleAsync(pipe, root, updates, maintenance, settings, window, cancellation, caller);
            }
            catch (Exception error) when (UpdateWindowPipe.IsFailure(error))
            {
                if (!cancellation.IsCancellationRequested && error is not EndOfStreamException)
                    Console.Error.WriteLine(UpdateWindowPipe.Reason(error));
            }
            finally { if (pipe.IsConnected) pipe.Disconnect(); }
        } while (caller is null && !cancellation.IsCancellationRequested);
    }

    private static async Task HandleAsync(NamedPipeServerStream pipe, string root, HostUpdateService updates,
        HostMaintenanceController maintenance, HostSettingsService settings, UpdateWindowIntent window,
        CancellationToken cancellation, int? caller)
    {
        var pid = UpdateWindowPipe.ClientProcess(pipe);
        if (caller is not null && caller != pid) throw new IOException("maintenance_window_peer_unbound");
        using var peer = BindPeer(pid, root);
        await maintenance.BindWindowAsync(peer, cancellation);
        try { await ServeSessionAsync(pipe, updates, maintenance, settings, window, cancellation, peer); }
        finally { maintenance.ReleaseWindow(peer); }
    }

    private static async Task ServeSessionAsync(NamedPipeServerStream pipe, HostUpdateService updates,
        HostMaintenanceController maintenance, HostSettingsService settings, UpdateWindowIntent window,
        CancellationToken cancellation, NativeClientBinding peer)
    {
        var channel = settings.Read().Effective.Updates.Channel == UpdateChannel.Stable
            ? HostUpdateChannel.Stable : HostUpdateChannel.Prerelease;
        await using var session = new UpdateWindowSession(updates, maintenance, channel, window);
        while (peer.IsLive)
        {
            var command = await UpdateWindowPipe.ReadCommandAsync(pipe, cancellation);
            var status = await session.ExecuteAsync(command, peer, cancellation);
            await UpdateWindowPipe.ReplyAsync(pipe, status, cancellation);
            if (status.Phase is UpdateWindowPhase.HandedOff or UpdateWindowPhase.Closed) return;
        }
    }

    internal static async Task<int> RunAsync(int caller)
    {
        try
        {
            var root = PhysicalPathPolicy.RuntimeApplicationDirectory();
            var parent = ProcessSnapshotProbe.Capture(Environment.ProcessId, "", CancellationToken.None).Processes.Single();
            if (caller != parent.Ppid || !StringComparer.OrdinalIgnoreCase.Equals(PhysicalPathPolicy.RuntimeProcessPath(),
                Path.Combine(root, ApplicationTree.HostEntry))) throw new IOException("maintenance_window_peer_unbound");
            using (BindPeer(caller, root)) { }
            var contracts = new ContractReader();
            var instance = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(root, "runtime"));
            using var lease = InstanceLease.Acquire(instance, contracts);
            var roots = new PayloadApplyRoots(root, Path.Combine(instance.RuntimeRoot, "extensions"));
            var runtime = RuntimeReadiness.Verify(instance, contracts, lease, roots);
            Directory.CreateDirectory(PhysicalPathPolicy.NormalizeWithoutLinks(roots.ExtensionPayloadRoot));
            var lifecycle = new HostLifecycle(runtime.Recovery, TimeProvider.System);
            var resources = new ResourceLeaseManager(lifecycle);
            var settings = HostStartupSettings.Create(instance, runtime.Database, lease, resources, runtime.Recovery);
            var secrets = new SecretFieldService(instance, runtime.Database, lease, resources, runtime.Recovery, contracts);
            var network = new ExtensionStoreNetwork(instance, runtime.Database, lease, resources, () => settings.Read().Effective.Proxy, secrets);
            await using var updates = new HostUpdateService(instance, runtime.Database, lease, resources, contracts, roots,
                UpdateReleaseTrust.Configured(), network.CreateClient, () => settings.Read().Effective.Updates.SourceUrl);
            await using var app = WebApplication.CreateSlimBuilder().Build();
            await using var maintenance = new HostMaintenanceController(instance, runtime.Database, lease, contracts,
                roots, lifecycle, resources, app.Lifetime, null, null);
            var mode = settings.Read().Effective.Desktop.Mode == DesktopMode.Desktop ? UpdateWindowMode.Desktop : UpdateWindowMode.Browser;
            await ServeAsync(root, updates, maintenance, settings, new(mode, true, "/"), app.Lifetime.ApplicationStopping, caller);
            return 0;
        }
        catch (Exception error) when (UpdateWindowPipe.IsFailure(error))
        {
            Console.Error.WriteLine(UpdateWindowPipe.Reason(error));
            return 1;
        }
    }

    private static NativeClientBinding BindPeer(int pid, string root)
    {
        using var process = JobNative.OpenProcess(0x1000, false, pid);
        if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(root, ApplicationTree.ManifestPath));
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > StrictJson.MaximumBytes) throw new IOException("maintenance_window_peer_unbound");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        var expected = ApplicationManifest.Read(new(), bytes, null).Files.Single(file => file.RelativePath == ApplicationTree.MaintenanceEntry);
        var image = ProcessIdentityLease.HoldImage(Path.Combine(root, ApplicationTree.MaintenanceEntry));
        try
        {
            if (image.Sha256 != expected.Sha256) throw new IOException("maintenance_window_peer_unbound");
            return new(image.Read(process, pid), image);
        }
        catch { image.Dispose(); throw; }
    }
}
