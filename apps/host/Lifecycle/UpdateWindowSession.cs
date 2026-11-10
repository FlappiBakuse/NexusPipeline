using System.Reflection;
using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Storage.Maintenance;

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
