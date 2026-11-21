using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Networking;

namespace NexusPipeline.Host.Lifecycle;

internal sealed class DesktopExitWatch(NativeClientBinding binding, HostLifecycle lifecycle, IHostApplicationLifetime application,
    Func<CancellationToken, Task>? waitForFamilyExit = null) : IAsyncDisposable
{
    private readonly object _gate = new();
    private Task? _watch;
    private bool _ending;
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(application.ApplicationStopping);

    public void Begin(HostClosePermit permit)
    {
        lock (_gate)
        {
            if (!_ending) _watch = WatchAsync(permit);
        }
    }

    private async Task WatchAsync(HostClosePermit permit)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var remaining = permit.ExpiresAt - TimeProvider.System.GetUtcNow();
        waiting.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        var finalizing = false;
        try
        {
            if (waitForFamilyExit is null) await binding.WaitForExitAsync(waiting.Token);
            else await waitForFamilyExit(waiting.Token);
            if (lifecycle.CanFinalizeAfterExit(permit.SessionId, permit.PermitId))
            {
                finalizing = true;
                application.StopApplication();
            }
            else Console.Error.WriteLine("desktop_exit_recovery_changed");
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (OperationCanceledException) when (waiting.IsCancellationRequested)
        {
            Console.Error.WriteLine("desktop_exit_permit_expired");
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            var reason = exception is IOException && exception.Message.StartsWith("worker_stopped_family_", StringComparison.Ordinal)
                ? exception.Message : exception.GetType().Name;
            Console.Error.WriteLine("desktop_exit_unverified:" + reason);
        }
        finally
        {
            if (!finalizing) lifecycle.AbandonClose(permit.SessionId, permit.CorrelationId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? watch;
        lock (_gate)
        {
            if (_ending) return;
            _ending = true;
            watch = _watch;
        }
        try
        {
            _stop.Cancel();
            if (watch is not null) await watch.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { _stop.Dispose(); }
    }
}
