using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Media;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class WindowCaptureProcess : IExecutionCaptureProcess
{
    private readonly OwnedWorkerJob _helper;
    private readonly OwnedPreviewJob? _preview;
    private readonly OwnedProcessOutput _output;
    private readonly PreparedPcWindow _window;
    private readonly ExecutionPlanCaptureRuntime _runtime;
    private readonly CapturePngSpool? _png;
    private readonly CaptureBatchSpool? _batch;
    private readonly WindowCaptureRequest _request;
    private readonly ExecutionTimeBudget _budget;
    private PreparedStoppedTarget? _stopped;
    private OwnedWorkerIdentity? _root;
    private Task? _readPng, _readError, _send;
    private bool _started, _finished, _disposed, _outputClosed;
    private Stopwatch? _clock;
    private CancellationToken _contextCancellation;
    private string _reason = "execution_capture_unavailable";
    private int? _exitCode;
    internal IReadOnlyList<CaptureFrameRecord> PublishedFrames { get; private set; } = [];
    public string JobName => _helper.Name;

    internal WindowCaptureProcess(RuntimeInstance instance, Guid captureId,
        PreparedPcWindow window, ExecutionPlanCaptureRuntime runtime, ExecutionTimeBudget budget, OwnedPreviewJob? preview = null, string captureMethod = "printWindow")
    {
        if (runtime.TimeoutMilliseconds != (runtime.MaximumFramesPerStep == 1 ? ExecutionCaptureInputs.TimeoutMilliseconds :
                ExecutionCaptureInputs.BatchTimeoutMilliseconds) || runtime.MaximumPngBytes != CapturePngSpool.MaximumBytes ||
            runtime.MaximumFramesPerStep is < 1 or > ExecutionCaptureInputs.MaximumBatchFrames)
            throw new IOException("execution_capture_runtime_unbound");
        var facts = window.Facts;
        if (facts.Binding.InstanceId != instance.InstanceId) throw new IOException("execution_capture_window_unbound");
        _window = window; _runtime = runtime; _budget = budget;
        _request = new("nexus.media.window-capture-request", runtime.MaximumFramesPerStep == 1 ? 1 : 2,
            captureId, instance.InstanceId, facts.Binding.AttemptId, facts.Process, facts.WindowHandle)
        { CaptureMethod = captureMethod == "printWindow" ? null : captureMethod, FrameCount = runtime.MaximumFramesPerStep == 1 ? null : checked((int)runtime.MaximumFramesPerStep) };
        _preview = preview; _helper = new();
        try
        {
            _output = new(redirectInput: true); CapturePngSpool? spool = null;
            try
            {
                if (runtime.MaximumFramesPerStep == 1) _png = spool = new(instance, captureId, facts.Width, facts.Height);
                else _batch = new(instance, captureId, facts.Width, facts.Height, checked((int)runtime.MaximumFramesPerStep));
                _helper.ObserveProcessIdentities(_ => { });
            }
            catch { spool?.Dispose(); _output.Dispose(); throw; }
        }
        catch { _helper.Dispose(); throw; }
    }

    public async Task CaptureAsync(Func<Func<OwnedWorkerJob, OwnedWorkerIdentity>, OwnedWorkerIdentity> useParent,
        Action<OwnedWorkerJob, OwnedWorkerIdentity> admitRoot, TimeSpan stopTimeout, CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) throw new IOException("execution_capture_already_started");
        _started = true;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(_runtime.TimeoutMilliseconds, Math.Max(1, _budget.Read().Remaining.TotalMilliseconds))));
        var clock = _clock = Stopwatch.StartNew(); _contextCancellation = cancellation;
        try
        {
            _root = _window.Use(_ => useParent(parent => ExecutionCaptureStarter.Start(_helper, parent, _preview, _runtime.Broker.Path,
                ["--window-capture-worker"], Path.GetDirectoryName(_runtime.Broker.Path)!, _request.InstanceId,
                _request.AttemptId, root =>
                {
                    _root = root;
                    if (root.InstanceId != _request.InstanceId || root.AttemptId != _request.AttemptId ||
                        root.ImagePath != _runtime.Broker.Path || root.ImageSha256 != _runtime.Broker.Sha256)
                        throw new IOException("execution_capture_native_root_unbound");
                    RequireDeadline(); admitRoot(_helper, root); RequireDeadline();
                }, _output)));
            _readPng = ReadFramesAsync();
            _readError = DrainErrorAsync(); _send = SendAsync();
            while (true)
            {
                RequireDeadline();
                if (_readPng.IsFaulted || _readError.IsFaulted || _send.IsFaulted)
                    throw new IOException("execution_capture_output_rejected");
                _exitCode = await _helper.AwaitRootAsync(_root, TimeSpan.FromMilliseconds(50), deadline.Token).ConfigureAwait(false);
                if (_exitCode is not null) break;
            }
            _reason = _exitCode == 0 ? "execution_capture_frame_pending" : "execution_capture_helper_failed";
        }
        catch (OperationCanceledException) { _reason = cancellation.IsCancellationRequested ? "execution_capture_context_closed" : "execution_capture_deadline"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { _reason = "execution_capture_unavailable"; }
        finally { await FinishAsync(stopTimeout).ConfigureAwait(false); }
        if (_reason == "execution_capture_frame_pending" && (cancellation.IsCancellationRequested ||
            clock.ElapsedMilliseconds >= _runtime.TimeoutMilliseconds))
            _reason = cancellation.IsCancellationRequested ? "execution_capture_context_closed" : "execution_capture_deadline";

        void RequireDeadline()
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (clock.ElapsedMilliseconds >= _runtime.TimeoutMilliseconds || _budget.Read().Remaining == TimeSpan.Zero)
                throw new OperationCanceledException(deadline.Token);
        }
    }

    public async Task FinishAsync(TimeSpan stopTimeout)
    {
        if (_finished) return;
        _started = true;
        _stopped ??= await PreparedStoppedTarget.StopAsync(_helper, stopTimeout).ConfigureAwait(false);
        if (!_outputClosed) _output.CloseUnstartedEnds();
        _readPng ??= ReadFramesAsync();
        _readError ??= DrainErrorAsync(); _send ??= Task.CompletedTask;
        var drainTimeout = _batch is null || _clock is null ? stopTimeout : TimeSpan.FromMilliseconds(Math.Max(1,
            Math.Min(_runtime.TimeoutMilliseconds - _clock.ElapsedMilliseconds,
                _budget.Read().Remaining.TotalMilliseconds)));
        try { await Task.WhenAll(_readPng, _readError, _send).WaitAsync(drainTimeout).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or TimeoutException or ObjectDisposedException or OperationCanceledException)
        {
            _reason = "execution_capture_output_incomplete";
            _output.Dispose(); _outputClosed = true;
            try { await Task.WhenAll(_readPng, _readError, _send).WaitAsync(stopTimeout).ConfigureAwait(false); }
            catch (Exception completion) when (completion is IOException or ObjectDisposedException or OperationCanceledException) { }
        }
        _finished = true;
    }

    public T UseStopped<T>(ImmutableBlobStore blobs, Func<OwnedWorkerIdentity?, OwnedTargetInventory, BlobReference?, string, CapturePngInfo?, T> publish)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_finished || _stopped is null) throw new IOException("execution_capture_collection_pending");
        return _stopped.Use(_helper, inventory =>
        {
            BlobReference? png = null;
            if (_exitCode == 0 && _reason == "execution_capture_frame_pending")
            {
                try
                {
                    if (_contextCancellation.IsCancellationRequested || _clock!.ElapsedMilliseconds >= _runtime.TimeoutMilliseconds ||
                        _budget.Read().Remaining == TimeSpan.Zero)
                        throw new IOException("execution_capture_context_closed");
                    if (_batch is null) png = _window.Use(_ => _png!.VerifyAndPersist(blobs));
                    else PublishedFrames = _window.Use(_ => _batch.Frames());
                    _window.Use(_ => true);
                    if (_contextCancellation.IsCancellationRequested || _clock.ElapsedMilliseconds >= _runtime.TimeoutMilliseconds ||
                        _budget.Read().Remaining == TimeSpan.Zero)
                        throw new IOException("execution_capture_context_closed");
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { png = null; PublishedFrames = []; _reason = "execution_capture_frame_rejected"; }
            }
            return publish(_root, inventory, png, png is null && PublishedFrames.Count == 0 ? _reason :
                "execution_capture_frame_verified", null);
        });
    }

    private Task ReadFramesAsync() => _batch is null ? _png!.ReadOutputAsync(_output.StandardOutput, CancellationToken.None) :
        _batch.ReadOutputAsync(_output.StandardOutput, CancellationToken.None);

    private async Task SendAsync()
    {
        try
        {
            var request = JsonSerializer.SerializeToUtf8Bytes(_request, WindowCaptureWorker.Options);
            if (request.Length > 32768) throw new IOException("execution_capture_request_byte_limit");
            await _output.StandardInput.WriteAsync(request).ConfigureAwait(false);
            await _output.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        finally { _output.CompleteInput(); }
    }

    public NexusPipeline.Core.Media.PreviewImage ReadLiveFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireFramePublication();
        var frame = _stopped!.Use(_helper, inventory => _window.Use(_ => _batch is null ? _png!.ReadLiveFrame() : _batch.ReadLiveFrame()));
        _window.Use(_ => true);
        RequireFramePublication();
        return frame;
    }

    public void RequireFramePublication()
    {
        if (!_finished || _stopped is null || _exitCode != 0 || _reason != "execution_capture_frame_pending" ||
            _contextCancellation.IsCancellationRequested || _clock is null || _clock.ElapsedMilliseconds >= _runtime.TimeoutMilliseconds ||
            _budget.Read().Remaining == TimeSpan.Zero) throw new IOException("execution_capture_deadline");
    }

    private async Task DrainErrorAsync()
    {
        var buffer = new byte[4096]; long observed = 0;
        while (true)
        {
            var count = await _output.StandardError.ReadAsync(buffer).ConfigureAwait(false);
            if (count == 0) return;
            observed += count;
            if (observed > 65536) throw new IOException("execution_capture_error_byte_limit");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (!_started && _root is null && _readPng is null && _readError is null && _send is null &&
            _helper.GetAccounting() is { ActiveProcesses: 0, TotalProcesses: 0 } && _helper.ActiveProcessIds().Count == 0)
        { _output.Dispose(); _helper.Dispose(); _png?.Dispose(); _batch?.Dispose(); _window.Dispose(); _disposed = true; return; }
        if (!_finished || _stopped is null || _readPng?.IsCompleted != true || _readError?.IsCompleted != true || _send?.IsCompleted != true)
            throw new IOException("execution_capture_collection_pending");
        _output.Dispose(); _helper.Dispose(); _png?.Dispose(); _batch?.Dispose(); _window.Dispose(); _disposed = true;
    }
}
