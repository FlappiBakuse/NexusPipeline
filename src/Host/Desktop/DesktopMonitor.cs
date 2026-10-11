namespace NexusPipeline.Host.Desktop;

internal static class DesktopMonitor
{
    internal static async Task RunAsync(Func<Task> observe, Action<Exception> failed, CancellationToken token,
        TimeSpan? interval = null)
    {
        using var timer = new PeriodicTimer(interval ?? TimeSpan.FromSeconds(1));
        bool failureReported = false;
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try { await observe().ConfigureAwait(false); failureReported = false; }
                catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                {
                    if (!failureReported) failed(exception);
                    failureReported = true;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
