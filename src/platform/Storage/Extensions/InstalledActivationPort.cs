using System.Runtime.Versioning;
using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Platform.Extensions;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class InstalledActivationPort(InstalledExtensionConsumption consumption, ExtensionScope scope,
    Task writerReleased, Action publishCurrent) : IExtensionActivation
{
    private readonly object _gate = new();
    private Task? _ready;

    public ValueTask WaitReadyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task ready;
        lock (_gate)
        {
            if (_ready is null)
            {
                using (ExecutionContext.SuppressFlow()) _ready = Task.Run(PrepareAsync);
                _ = _ready.ContinueWith(static fault => { _ = fault.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            ready = _ready;
        }
        return new(ready.WaitAsync(cancellationToken));
    }

    private async Task PrepareAsync()
    {
        await writerReleased.WaitAsync(scope.Cancellation).ConfigureAwait(false);
        scope.Cancellation.ThrowIfCancellationRequested();
        using var lifetime = scope.BeginUse();
        publishCurrent();
        using var use = await consumption.BeginAsync(scope.Cancellation).ConfigureAwait(false);
        use.RequireCurrent();
    }
}
