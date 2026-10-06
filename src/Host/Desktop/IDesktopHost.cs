namespace NexusPipeline.Host.Desktop;

internal interface IDesktopHost
{
    void Start();
    void MarkReady(int actualPort);
    Task<bool> ShowAsync(string reason, CancellationToken token = default);
    Task PrepareHostRestartAsync(string handoffId, CancellationToken token = default);
    Task<bool> PrepareAssetReplacementAsync(string transactionId, TimeSpan remaining, CancellationToken token = default);
    Task StopForHostExitAsync(CancellationToken token = default);
}
