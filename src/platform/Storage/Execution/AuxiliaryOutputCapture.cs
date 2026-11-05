using System.Runtime.Versioning;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class AuxiliaryOutputCapture : IDisposable
{
    private readonly RawOutputRecorder _raw;
    private readonly OwnedProcessOutput _output = new();
    private readonly ProcessOutputCapture _capture;
    private readonly Task _lines;
    private bool _attempted, _disposed;
    private readonly string _startClosed, _collectionPending;

    internal AuxiliaryOutputCapture(ImmutableBlobStore blobs, StepEvidenceBinding binding, long remainingBytes,
        string startClosed = "execution_auxiliary_start_closed", string collectionPending = "execution_auxiliary_output_collection_pending",
        BindingRawAllowance? bindingAllowance = null, LiveLogWriter? live = null)
    {
        _startClosed = startClosed; _collectionPending = collectionPending;
        _raw = new(blobs, binding, maximumRecordedBytes: remainingBytes, bindingAllowance: bindingAllowance, live: live);
        _capture = new(_output, _raw); _lines = DrainLinesAsync();
    }

    internal void Start(Action<OwnedProcessOutput> start)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_attempted) throw new IOException(_startClosed);
        _attempted = true;
        try { start(_output); }
        catch { _output.CloseUnstartedEnds(); throw; }
    }

    internal async Task<BlobReference> CollectAsync(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_attempted) _output.CloseUnstartedEnds();
        var drain = await _capture.DrainAsync(timeout).ConfigureAwait(false);
        if (drain.DeadlineExceeded) throw new IOException(_collectionPending);
        await _lines.ConfigureAwait(false);
        return await _raw.Completion.WaitAsync(timeout).ConfigureAwait(false);
    }

    private async Task DrainLinesAsync()
    { await foreach (var _ in _capture.Lines.ReadAllAsync().ConfigureAwait(false)) { } }

    public void Dispose()
    {
        if (_disposed) return;
        if (!_capture.Completion.IsCompleted || !_lines.IsCompleted || !_raw.Completion.IsCompleted)
            throw new IOException(_collectionPending);
        _raw.Dispose(); _output.Dispose(); _disposed = true;
    }
}
