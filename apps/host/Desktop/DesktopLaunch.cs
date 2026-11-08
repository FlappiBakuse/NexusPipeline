using NexusPipeline.Platform.Processes;

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
