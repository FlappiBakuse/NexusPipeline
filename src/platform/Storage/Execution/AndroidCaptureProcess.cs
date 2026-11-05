using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Media;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class AndroidCaptureProcess : IExecutionCaptureProcess
{
    private readonly OwnedWorkerJob _helper;
    private readonly OwnedPreviewJob? _preview;
    private readonly OwnedProcessOutput _output;
    private readonly CapturePngSpool _png;
    private readonly PreparedCaptureContext _source;
    private readonly ExecutionTimeBudget _budget;
    private readonly Guid _instance, _attempt;
    private readonly ExecutionPlanInterpreterImage _tool;
    private readonly string _endpoint;
    private PreparedStoppedTarget? _stopped;
    private OwnedWorkerIdentity? _root;
    private Task? _readPng, _readError;
    private Stopwatch? _clock;
    private CancellationToken _cancellation;
    private bool _started, _finished, _disposed, _outputClosed, _naturalComplete;
    private int? _exitCode;
    private string _reason = "execution_capture_unavailable";
    public string JobName => _helper.Name;

    internal AndroidCaptureProcess(RuntimeInstance instance, Guid captureId,
        PreparedCaptureContext source, ExecutionTimeBudget budget, OwnedPreviewJob? preview = null)
    {
        _source = source; _budget = budget;
        var frozen = source.ReadFrozen();
        _tool = frozen.Tool; _endpoint = frozen.Facts.Endpoint;
        _instance = instance.InstanceId; _attempt = frozen.Facts.Binding.AttemptId;
        if (frozen.Facts.Binding.InstanceId != _instance) throw new IOException("execution_capture_android_unbound");
        _preview = preview; _helper = new();
        try
        {
            _output = new();
            CapturePngSpool? spool = null;
            try
            {
                _png = spool = new(instance, captureId);
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
        _started = true; _cancellation = cancellation; _clock = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(ExecutionCaptureInputs.TimeoutMilliseconds,
            Math.Max(1, _budget.Read().Remaining.TotalMilliseconds))));
        try
        {
            RequireDeadline(deadline.Token);
            await _source.RequireCurrentAsync(deadline.Token).ConfigureAwait(false);
            RequireDeadline(deadline.Token);
            _root = _source.Use(_ => useParent(parent => ExecutionCaptureStarter.Start(_helper, parent, _preview, _tool.Path,
                ["-s", _endpoint, "exec-out", "screencap", "-p"], Path.GetDirectoryName(_tool.Path)!,
                _instance, _attempt, root =>
                {
                    _root = root;
                    if (root.InstanceId != _instance || root.AttemptId != _attempt ||
                        !StringComparer.OrdinalIgnoreCase.Equals(root.ImagePath, _tool.Path) || root.ImageSha256 != _tool.Sha256)
                        throw new IOException("execution_capture_native_root_unbound");
                    RequireDeadline(deadline.Token); admitRoot(_helper, root); RequireDeadline(deadline.Token);
                }, _output)));
            _readPng = _png.ReadOutputAsync(_output.StandardOutput, CancellationToken.None);
            _readError = DrainErrorAsync();
            while (_exitCode is null)
            {
                RequireDeadline(deadline.Token);
                if (_readPng.IsFaulted || _readError.IsFaulted) throw new IOException("execution_capture_output_rejected");
                _exitCode = await _helper.AwaitRootAsync(_root, TimeSpan.FromMilliseconds(50), deadline.Token).ConfigureAwait(false);
            }
            await Task.WhenAll(_readPng, _readError).WaitAsync(deadline.Token).ConfigureAwait(false);
            RequireDeadline(deadline.Token); _naturalComplete = true;
            _reason = _exitCode == 0 ? "execution_capture_frame_pending" : "execution_capture_helper_failed";
        }
        catch (OperationCanceledException)
        { _reason = cancellation.IsCancellationRequested ? "execution_capture_context_closed" : "execution_capture_deadline"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { _reason = "execution_capture_output_rejected"; }
        finally { await FinishAsync(stopTimeout).ConfigureAwait(false); }
    }

    public async Task FinishAsync(TimeSpan stopTimeout)
    {
        if (_finished) return;
        _started = true;
        _stopped ??= await PreparedStoppedTarget.StopAsync(_helper, stopTimeout).ConfigureAwait(false);
        if (!_outputClosed) _output.CloseUnstartedEnds();
        _readPng ??= _png.ReadOutputAsync(_output.StandardOutput, CancellationToken.None);
        _readError ??= DrainErrorAsync();
        try { await Task.WhenAll(_readPng, _readError).WaitAsync(stopTimeout).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or TimeoutException or ObjectDisposedException or OperationCanceledException)
        {
            _naturalComplete = false; _reason = "execution_capture_output_incomplete";
            _output.Dispose(); _outputClosed = true;
            try { await Task.WhenAll(_readPng, _readError).WaitAsync(stopTimeout).ConfigureAwait(false); }
            catch (Exception completion) when (completion is IOException or ObjectDisposedException or OperationCanceledException) { }
        }
        _finished = true;
    }

    public T UseStopped<T>(ImmutableBlobStore blobs,
        Func<OwnedWorkerIdentity?, OwnedTargetInventory, BlobReference?, string, CapturePngInfo?, T> publish)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_finished || _stopped is null) throw new IOException("execution_capture_collection_pending");
        return _stopped.Use(_helper, inventory =>
        {
            BlobReference? png = null; CapturePngInfo? info = null;
            if (_naturalComplete && _exitCode == 0 && _reason == "execution_capture_frame_pending")
            {
                try
                {
                    RequireFramePublication();
                    info = _png.ReadVerifiedInfo(); png = _png.VerifyAndPersist(blobs);
                    RequireFramePublication();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { png = null; info = null; _reason = "execution_capture_frame_rejected"; }
            }
            return publish(_root, inventory, png, png is null ? _reason : "execution_capture_frame_verified", info);
        });
    }

    public NexusPipeline.Core.Media.PreviewImage ReadLiveFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireFramePublication();
        var frame = _stopped!.Use(_helper, inventory => _source.Use(_ => _png.ReadLiveFrame()));
        _source.Use(_ => true);
        RequireFramePublication();
        return frame;
    }

    public void RequireFramePublication()
    {
        if (!_finished || _stopped is null || !_naturalComplete || _exitCode != 0 ||
            _reason != "execution_capture_frame_pending") throw new IOException("execution_capture_frame_rejected");
        if (_cancellation.IsCancellationRequested || _clock is null ||
            _clock.ElapsedMilliseconds >= ExecutionCaptureInputs.TimeoutMilliseconds || _budget.Read().Remaining == TimeSpan.Zero)
            throw new IOException("execution_capture_deadline");
    }

    private void RequireDeadline(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (_clock is null || _clock.ElapsedMilliseconds >= ExecutionCaptureInputs.TimeoutMilliseconds ||
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
        if (!_started && _root is null && _readPng is null && _readError is null &&
            _helper.GetAccounting() is { ActiveProcesses: 0, TotalProcesses: 0 } && _helper.ActiveProcessIds().Count == 0)
        { _output.Dispose(); _helper.Dispose(); _png.Dispose(); _disposed = true; return; }
        if (!_finished || _stopped is null || _readPng?.IsCompleted != true || _readError?.IsCompleted != true)
            throw new IOException("execution_capture_collection_pending");
        _output.Dispose(); _helper.Dispose(); _png.Dispose(); _disposed = true;
    }
}
