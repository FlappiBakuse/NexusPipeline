using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Media;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class MuMuCaptureProcess : IExecutionCaptureProcess
{
    private readonly OwnedWorkerJob _helper;
    private readonly OwnedProcessOutput _output;
    private readonly CapturePngSpool _png;
    private readonly PreparedCaptureContext _source;
    private readonly ExecutionTimeBudget _budget;
    private readonly ImmutableBlobStore _blobs;
    private readonly ExecutionPlanInterpreterImage _broker, _manager;
    private readonly ExecutionPlanInterpreterImage? _tool;
    private readonly AndroidCaptureFacts _facts;
    private ExecutionCaptureBinding? _binding;
    private ExecutionCaptureStore? _store;
    private MuMuCaptureClient? _client;
    private PreparedStoppedTarget? _stopped;
    private OwnedWorkerIdentity? _root;
    private Task? _exchange, _error, _admissionWrite;
    private Stopwatch? _clock;
    private CancellationToken _cancellation;
    private bool _started, _finished, _disposed, _naturalComplete, _outputClosed;
    private int? _exitCode;
    private string _reason = "execution_capture_unavailable";
    public string JobName => _helper.Name;

    internal MuMuCaptureProcess(RuntimeInstance instance, Guid captureId,
        PreparedCaptureContext source, ExecutionTimeBudget budget, ImmutableBlobStore blobs)
    {
        _source = source; _budget = budget; _blobs = blobs;
        var frozen = source.ReadMuMuFrozen();
        _broker = frozen.Broker; _manager = frozen.Manager; _tool = frozen.Tool; _facts = frozen.Facts;
        _helper = new();
        try
        {
            _output = new(redirectInput: true);
            CapturePngSpool? spool = null;
            try { _png = spool = new(instance, captureId); _helper.ObserveProcessIdentities(_ => { }); }
            catch { spool?.Dispose(); _output.Dispose(); throw; }
        }
        catch { _helper.Dispose(); throw; }
    }

    internal void Bind(ExecutionCaptureStore store, ExecutionCaptureBinding binding)
    {
        if (_started || _store is not null || binding.Primary != _source.Primary || binding.HelperJobName != _helper.Name)
            throw new IOException("execution_capture_mumu_unbound");
        _store = store; _binding = binding;
    }

    public async Task CaptureAsync(Func<Func<OwnedWorkerJob, OwnedWorkerIdentity>, OwnedWorkerIdentity> useParent,
        Action<OwnedWorkerJob, OwnedWorkerIdentity> admitRoot, TimeSpan stopTimeout, CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started || _store is null || _binding is null) throw new IOException("execution_capture_already_started");
        _started = true; _clock = Stopwatch.StartNew(); _cancellation = cancellation;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(Remaining());
        try
        {
            RequireDeadline(deadline.Token);
            if (_tool is null) { _reason = "execution_capture_tool_unconfigured"; return; }
            _root = _source.Use(_ => useParent(parent => _helper.StartNested(parent, _broker.Path,
                ["--mumu-capture-worker"], Path.GetDirectoryName(_broker.Path)!, _binding.Primary.InstanceId,
                _binding.Primary.AttemptId, root =>
                {
                    _root = root; RequireDeadline(deadline.Token);
                    if (!StringComparer.OrdinalIgnoreCase.Equals(root.ImagePath, _broker.Path) || root.ImageSha256 != _broker.Sha256)
                        throw new IOException("execution_capture_native_root_unbound");
                    admitRoot(_helper, root); RequireDeadline(deadline.Token);
                }, _output)));
            var context = _store.MuMuContext();
            var request = new MuMuCaptureRequest(MuMuCaptureProtocol.RequestFormat, 1, _binding,
                context.Source, context.Root, Identity(_root), _manager, _tool, _facts.MuMu!.Index, _facts.Endpoint,
                Math.Max(1, (int)Remaining().TotalMilliseconds), Math.Clamp((int)stopTimeout.TotalMilliseconds, 1, 60000));
            _client = new(_helper, request, _blobs, _png);
            _error = DrainErrorAsync();
            _exchange = ExchangeAsync(_client, useParent, deadline.Token);
            await _exchange.WaitAsync(deadline.Token).ConfigureAwait(false);
            _output.CompleteInput();
            while (_exitCode is null)
            {
                RequireDeadline(deadline.Token);
                _exitCode = await _helper.AwaitRootAsync(_root, TimeSpan.FromMilliseconds(25), deadline.Token).ConfigureAwait(false);
            }
            var tail = new byte[1];
            if (await _output.StandardOutput.ReadAsync(tail, deadline.Token).ConfigureAwait(false) != 0)
                throw MuMuCaptureProtocol.Invalid();
            await _error.WaitAsync(deadline.Token).ConfigureAwait(false);
            RequireDeadline(deadline.Token); _naturalComplete = true;
            if (_exitCode != 0) _reason = "execution_capture_helper_failed";
        }
        catch (OperationCanceledException)
        { _reason = cancellation.IsCancellationRequested ? "execution_capture_context_closed" : "execution_capture_deadline"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        { _reason = error is IOException { Message: "execution_capture_deadline" } ? "execution_capture_deadline" : "execution_capture_output_rejected"; }
        finally { await FinishAsync(stopTimeout).ConfigureAwait(false); }
    }

    private async Task ExchangeAsync(MuMuCaptureClient client,
        Func<Func<OwnedWorkerJob, OwnedWorkerIdentity>, OwnedWorkerIdentity> useParent, CancellationToken cancellation)
    {
        var result = await client.RunAsync(_output.StandardOutput, _output.StandardInput,
            (child, write, token) =>
            {
                BlobReference? artifact = null;
                _source.Use(_ => useParent(parent => parent.UseObservedMember(child.ProcessIdentity, () =>
                    _helper.UseObservedMember(child.ProcessIdentity, () =>
                    {
                        RequireDeadline(token);
                        artifact = _store!.ChildRoot(_helper, child, Primary(parent));
                        _store.AdmitChildWrite(_helper, child, Primary(parent),
                            current => _admissionWrite = write(artifact!, current), Remaining(), token).GetAwaiter().GetResult();
                        return _root!;
                    }))));
                return Task.FromResult(artifact ?? throw MuMuCaptureProtocol.Invalid());
            }, observed => _store!.ChildObserved(_helper, observed), cancellation).ConfigureAwait(false);
        _reason = result.Outcome == "completed" ? "execution_capture_frame_pending" : result.ReasonCode;
    }

    private OwnedWorkerIdentity Primary(OwnedWorkerJob parent)
    {
        if (_primary is null || parent.Name != _binding!.Primary.JobName ||
            _primary.InstanceId != _binding.Primary.InstanceId || _primary.AttemptId != _binding.Primary.AttemptId) throw MuMuCaptureProtocol.Invalid();
        return _primary;
    }
    private OwnedWorkerIdentity? _primary;
    internal void BindPrimary(OwnedWorkerIdentity primary) => _primary = primary;

    public async Task FinishAsync(TimeSpan stopTimeout)
    {
        if (_finished) return; _started = true;
        try { _stopped ??= await PreparedStoppedTarget.StopAsync(_helper, stopTimeout).ConfigureAwait(false); }
        finally
        {
            if (!_outputClosed)
            {
                _output.CloseUnstartedEnds(); _output.Dispose(); _outputClosed = true;
            }
            if (_admissionWrite is not null) await ObserveAsync(_admissionWrite, stopTimeout).ConfigureAwait(false);
            if (_exchange is not null) await ObserveAsync(_exchange, stopTimeout).ConfigureAwait(false);
            if (_error is not null) await ObserveAsync(_error, stopTimeout).ConfigureAwait(false);
        }
        _finished = true;
    }

    public T UseStopped<T>(ImmutableBlobStore blobs,
        Func<OwnedWorkerIdentity?, OwnedTargetInventory, BlobReference?, string, CapturePngInfo?, T> publish)
    {
        if (!_finished || _stopped is null) throw new IOException("execution_capture_collection_pending");
        return _stopped.Use(_helper, inventory =>
        {
            BlobReference? png = null; CapturePngInfo? info = null;
            if (_naturalComplete && _exitCode == 0 && _reason == "execution_capture_frame_pending")
            {
                try
                {
                    RequireFramePublication(); info = _png.ReadVerifiedInfo(); png = _png.VerifyAndPersist(blobs);
                    RequireFramePublication();
                }
                catch (IOException error)
                {
                    png = null; info = null;
                    _reason = error.Message == "execution_capture_deadline" ? error.Message : "execution_capture_frame_rejected";
                }
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
        if (!_finished || _stopped is null || !_naturalComplete || _exitCode != 0 || _reason != "execution_capture_frame_pending")
            throw new IOException("execution_capture_frame_rejected");
        RequireDeadline(_cancellation);
    }

    private TimeSpan Remaining() => TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(
        ExecutionCaptureInputs.TimeoutMilliseconds - (_clock?.Elapsed.TotalMilliseconds ?? 0), _budget.Read().Remaining.TotalMilliseconds)));
    private void RequireDeadline(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_clock is null || _clock.ElapsedMilliseconds >= ExecutionCaptureInputs.TimeoutMilliseconds ||
            _budget.Read().Remaining == TimeSpan.Zero) throw new IOException("execution_capture_deadline");
    }
    private async Task DrainErrorAsync()
    {
        long bytes = 0; var buffer = new byte[4096];
        while (true)
        {
            var count = await _output.StandardError.ReadAsync(buffer).ConfigureAwait(false);
            if (count == 0) return;
            if ((bytes += count) > 65536) throw new IOException("execution_capture_error_byte_limit");
        }
    }
    private static async Task ObserveAsync(Task task, TimeSpan timeout)
    {
        try { await task.WaitAsync(timeout).ConfigureAwait(false); }
        catch (Exception error) when (task.IsCompleted && error is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { }
    }
    private static ProcessIdentity Identity(OwnedWorkerIdentity root) =>
        new(root.ProcessId, root.CreatedAtUtc, root.ImagePath, root.ImageSha256, root.UserSid, root.IsElevated);

    public void Dispose()
    {
        if (_disposed) return;
        if (!_started && _root is null && _helper.GetAccounting() is { ActiveProcesses: 0, TotalProcesses: 0 } &&
            _helper.ActiveProcessIds().Count == 0)
        { _output.Dispose(); _helper.Dispose(); _png.Dispose(); _disposed = true; return; }
        if (!_finished || _stopped is null || _admissionWrite is { IsCompleted: false } || _exchange is { IsCompleted: false } || _error is { IsCompleted: false })
            throw new IOException("execution_capture_collection_pending");
        _client?.Dispose(); _output.Dispose(); _helper.Dispose(); _png.Dispose(); _disposed = true;
    }
}
