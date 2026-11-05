using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Media;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Media;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class MuMuLiveProcess : ILiveCaptureProcess
{
    private readonly OwnedWorkerJob _helper;
    private readonly OwnedPreviewJob? _preview;
    private readonly OwnedProcessOutput _output;
    private readonly CapturePngSpool _png;
    private readonly PreparedCaptureContext _source;
    private readonly ExecutionTimeBudget _budget;
    private readonly ExecutionPlanInterpreterImage _broker, _manager;
    private readonly ExecutionPlanInterpreterImage? _tool;
    private readonly AndroidCaptureFacts _facts;
    private readonly ExecutionCaptureBinding _binding;
    private MuMuLiveClient? _client;
    private PreparedStoppedTarget? _stopped;
    private Task? _exchange, _error;
    private OwnedWorkerIdentity? _root;
    private Stopwatch? _clock;
    private CancellationToken _cancellation;
    private bool _started, _finished, _disposed, _natural, _outputClosed;
    private int? _exitCode;
    private string _reason = "execution_capture_unavailable";

    internal MuMuLiveProcess(RuntimeInstance instance, Guid id, PreparedCaptureContext source,
        ExecutionTimeBudget budget, StepEvidenceBinding primary, string totalDigest, OwnedPreviewJob? preview = null)
    {
        _source = source; _budget = budget;
        var frozen = source.ReadMuMuFrozen();
        _broker = frozen.Broker; _manager = frozen.Manager; _tool = frozen.Tool; _facts = frozen.Facts;
        if (instance.InstanceId != primary.InstanceId || source.Primary != primary || id == Guid.Empty)
            throw MuMuLiveProtocol.Invalid();
        _preview = preview; _helper = new();
        _binding = new(primary, id, totalDigest, _helper.Name);
        try
        {
            _output = new(redirectInput: true);
            CapturePngSpool? spool = null;
            try { _png = spool = new(instance, id); _helper.ObserveProcessIdentities(_ => { }); }
            catch { spool?.Dispose(); _output.Dispose(); throw; }
        }
        catch { _helper.Dispose(); throw; }
    }

    public async Task CaptureAsync(Func<Func<OwnedWorkerJob, OwnedWorkerIdentity>, OwnedWorkerIdentity> useParent,
        Action<OwnedWorkerJob, OwnedWorkerIdentity> admitRoot, TimeSpan stopTimeout, CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) throw MuMuLiveProtocol.Invalid();
        _started = true; _clock = Stopwatch.StartNew(); _cancellation = cancellation;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(Remaining());
        try
        {
            RequireDeadline(deadline.Token);
            if (_tool is null) throw new IOException("execution_capture_tool_unconfigured");
            _root = _source.Use(_ => useParent(parent => ExecutionCaptureStarter.Start(_helper, parent, _preview, _broker.Path,
                ["--mumu-live-capture-worker"], Path.GetDirectoryName(_broker.Path)!, _binding.Primary.InstanceId,
                _binding.Primary.AttemptId, root =>
                {
                    _root = root; RequireDeadline(deadline.Token);
                    if (!StringComparer.OrdinalIgnoreCase.Equals(root.ImagePath, _broker.Path) || root.ImageSha256 != _broker.Sha256)
                        throw MuMuLiveProtocol.Invalid();
                    admitRoot(_helper, root); RequireDeadline(deadline.Token);
                }, _output)));
            var request = new MuMuLiveRequest(MuMuLiveProtocol.RequestFormat, 1, _binding, Identity(_root),
                _manager, _tool, _facts.MuMu!.Index, _facts.Endpoint, Math.Max(1, (int)Remaining().TotalMilliseconds),
                Math.Clamp((int)stopTimeout.TotalMilliseconds, 1, 60000));
            _client = new(_helper, request, _png); _error = DrainErrorAsync();
            _exchange = ExchangeAsync(useParent, deadline.Token);
            await _exchange.WaitAsync(deadline.Token).ConfigureAwait(false); _output.CompleteInput();
            while (_exitCode is null)
            {
                RequireDeadline(deadline.Token);
                _exitCode = await _helper.AwaitRootAsync(_root, TimeSpan.FromMilliseconds(25), deadline.Token).ConfigureAwait(false);
            }
            if (await _output.StandardOutput.ReadAsync(new byte[1], deadline.Token).ConfigureAwait(false) != 0) throw MuMuLiveProtocol.Invalid();
            await _error.WaitAsync(deadline.Token).ConfigureAwait(false);
            RequireDeadline(deadline.Token); _natural = true;
        }
        finally { await FinishAsync(stopTimeout).ConfigureAwait(false); }
    }

    private async Task ExchangeAsync(Func<Func<OwnedWorkerJob, OwnedWorkerIdentity>, OwnedWorkerIdentity> useParent, CancellationToken token)
    {
        var result = await _client!.RunAsync(_output.StandardOutput, _output.StandardInput, (child, admit) =>
            _source.Use(_ => useParent(parent => UseMember(parent, child.ProcessIdentity, () =>
                _helper.UseObservedMember(child.ProcessIdentity, () =>
                {
                    RequireDeadline(token); child.RequireAwaitingMember(_helper);
                    admit(); RequireDeadline(token); return _root!;
                })))), token).ConfigureAwait(false);
        _reason = result.ReasonCode;
    }

    private T UseMember<T>(OwnedWorkerJob parent, ProcessIdentity member, Func<T> callback) =>
        _preview is null ? parent.UseObservedMember(member, callback) : _preview.UseObservedMember(member, callback);

    public async Task FinishAsync(TimeSpan stopTimeout)
    {
        if (_finished) return; _started = true;
        _stopped ??= await PreparedStoppedTarget.StopAsync(_helper, stopTimeout).ConfigureAwait(false);
        if (!_outputClosed) { _output.CloseUnstartedEnds(); _output.Dispose(); _outputClosed = true; }
        if (_exchange is not null) await ObserveAsync(_exchange, stopTimeout).ConfigureAwait(false);
        if (_error is not null) await ObserveAsync(_error, stopTimeout).ConfigureAwait(false);
        _finished = true;
    }

    public PreviewImage ReadLiveFrame()
    {
        RequireFramePublication();
        var image = _stopped!.Use(_helper, inventory => _source.Use(facts => _png.ReadLiveFrame()));
        _source.Use(_ => true); RequireFramePublication(); return image;
    }

    public void RequireFramePublication()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_finished || !_natural || _stopped is null || _exitCode != 0 || _reason != "execution_capture_frame_verified")
            throw new IOException("mumu_live_capture_frame_unavailable");
        RequireDeadline(_cancellation);
    }

    private TimeSpan Remaining() => TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(
        ExecutionCaptureInputs.TimeoutMilliseconds - (_clock?.Elapsed.TotalMilliseconds ?? 0), _budget.Read().Remaining.TotalMilliseconds)));
    private void RequireDeadline(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_clock is null || _clock.ElapsedMilliseconds >= ExecutionCaptureInputs.TimeoutMilliseconds || _budget.Read().Remaining == TimeSpan.Zero)
            throw new IOException("execution_capture_deadline");
    }
    private async Task DrainErrorAsync()
    {
        long count = 0; var buffer = new byte[4096];
        while (true)
        {
            var read = await _output.StandardError.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0) return;
            if ((count += read) > 65536) throw new IOException("execution_capture_error_byte_limit");
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
        if (!_started && _root is null && _helper.GetAccounting() is { ActiveProcesses: 0, TotalProcesses: 0 })
        { _output.Dispose(); _helper.Dispose(); _png.Dispose(); _disposed = true; return; }
        if (!_finished || _stopped is null || _exchange is { IsCompleted: false } || _error is { IsCompleted: false })
            throw new IOException("media_helper_collection_pending");
        _client?.Dispose(); _output.Dispose(); _helper.Dispose(); _png.Dispose(); _disposed = true;
    }
}
