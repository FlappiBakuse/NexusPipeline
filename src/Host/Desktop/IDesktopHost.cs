using NexusPipeline.Modules.Updates;

namespace NexusPipeline.Host.Desktop;

internal interface IDesktopHost
{
    void Start();
    void MarkReady(int actualPort);
    Task<bool> ShowAsync(string reason, CancellationToken token = default);
    bool SavedLightweightMode { get; }
    Task<string> SetLightweightModeAsync(bool value, CancellationToken token = default);
    Task<string> ReloadPageAsync(CancellationToken token = default);
    Task PrepareHostRestartAsync(string handoffId, CancellationToken token = default);
    Task AbortHostRestartAsync(string handoffId, CancellationToken token = default);
    DesktopResumeIntent CaptureResumeIntent();
    Task<bool> PrepareAssetReplacementAsync(string transactionId, TimeSpan remaining, CancellationToken token = default);
    Task<bool> AbortAssetReplacementAsync(string transactionId, DesktopResumeIntent intent, CancellationToken token = default);
    Task StopForHostExitAsync(CancellationToken token = default);
}
