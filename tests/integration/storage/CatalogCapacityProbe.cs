using System.Diagnostics;

namespace NexusPipeline.StorageTests;

internal sealed record RecordCapacityObservation(long Milliseconds, double CpuMilliseconds, long AllocatedBytes);

internal sealed class CatalogCapacityProbe : IAsyncDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _gate = new();
    private readonly Task _sampling;
    private long _samples, _maxManaged, _maxHeap, _minWorking = long.MaxValue, _maxWorking, _minPrivate = long.MaxValue, _maxPrivate;

    internal CatalogCapacityProbe()
    {
        Sample(); _sampling = Task.Run(SampleLoopAsync);
    }

    internal RecordCapacityObservation Observe()
    {
        lock (_gate)
        {
            SampleCore();
            return new(_clock.ElapsedMilliseconds, _process.TotalProcessorTime.TotalMilliseconds,
                GC.GetTotalAllocatedBytes(precise: true));
        }
    }

    internal object SampledRanges()
    {
        lock (_gate) return new
        {
            intervalMilliseconds = 100, samples = _samples,
            maximumSampledManagedEstimateBytes = _maxManaged, maximumSampledGcHeapBytes = _maxHeap,
            minimumSampledTesthostWorkingSetBytes = _minWorking, maximumSampledTesthostWorkingSetBytes = _maxWorking,
            minimumSampledTesthostPrivateBytes = _minPrivate, maximumSampledTesthostPrivateBytes = _maxPrivate
        };
    }

    private async Task SampleLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try { while (await timer.WaitForNextTickAsync(_cancellation.Token)) Sample(); }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
    }

    private void Sample() { lock (_gate) SampleCore(); }

    private void SampleCore()
    {
        _process.Refresh(); _samples++;
        _maxManaged = Math.Max(_maxManaged, GC.GetTotalMemory(forceFullCollection: false));
        _maxHeap = Math.Max(_maxHeap, GC.GetGCMemoryInfo().HeapSizeBytes);
        _minWorking = Math.Min(_minWorking, _process.WorkingSet64); _maxWorking = Math.Max(_maxWorking, _process.WorkingSet64);
        _minPrivate = Math.Min(_minPrivate, _process.PrivateMemorySize64); _maxPrivate = Math.Max(_maxPrivate, _process.PrivateMemorySize64);
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        try { await _sampling.ConfigureAwait(false); }
        finally { _cancellation.Dispose(); _process.Dispose(); }
    }
}
