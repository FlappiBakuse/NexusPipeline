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

    public void Begin(Guid sessionId)
    {
        lock (_gate)
        {
            if (!_ending && (_watch is null || _watch.IsCompleted)) _watch = WatchAsync(sessionId);
        }
    }

    private async Task WatchAsync(Guid sessionId)
    {
        try
        {
            if (waitForFamilyExit is null) await binding.WaitForExitAsync(_stop.Token);
            else await waitForFamilyExit(_stop.Token);
            if (lifecycle.CanFinalizeAfterExit(sessionId)) application.StopApplication();
            else Console.Error.WriteLine("desktop_exit_recovery_changed");
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            var reason = exception is IOException && exception.Message.StartsWith("worker_stopped_family_", StringComparison.Ordinal)
                ? exception.Message : exception.GetType().Name;
            Console.Error.WriteLine("desktop_exit_unverified:" + reason);
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
