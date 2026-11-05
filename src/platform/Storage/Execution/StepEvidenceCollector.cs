using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Automation;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class StepEvidenceCollector : IDisposable
{
    private readonly StepEvidenceBinding _binding;
    private readonly StepEvidenceFactory _factory;
    private readonly AttemptTerminalStore _terminal;
    private readonly ConfigurationWriterProcess? _writer;
    private readonly ConfigurationRunController? _controller;
    private readonly RawOutputRecorder _raw;
    private readonly ProcessOutputCapture _capture;
    private readonly ExecutionOutputActivity _activity;
    private readonly AutomationLimits _limits;
    private readonly bool _observeStall;
    private readonly AttemptBusinessJudgement _judgement;
    private readonly StepJudgementWorker? _worker;
    private readonly ProviderControlSession? _provider;
    private BlobReference? _providerControl;
    private readonly Func<BlobReference, ValueTask<BlobReference?>>? _observeAdapter;
    private BlobReference? _adapterObservation;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly OwnedProcessOutput _output;
    private readonly Task _lines;
    private readonly Task _pipeLines;
    private readonly FileLogTailer? _files;
    private BlobReference? _observation;
    private ExecutionStepEvidence? _latest;
    private readonly TaskCompletionSource<BusinessJudgementDecision> _business = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _businessTimestamp;
    private StepStopProof? _stopProof;
    private bool _started;
    private bool _disposed;
    internal string? ObservationFailureCode { get; private set; }
    internal Task<BusinessJudgementDecision> BusinessAccepted => _business.Task;
    internal Task<BlobReference> SnapshotOutputAsync() => _raw.SnapshotAsync();
    internal TimeSpan? ElapsedSinceAcceptedBusiness
    {
        get
        {
            var timestamp = Interlocked.Read(ref _businessTimestamp);
            return timestamp == 0 ? null : System.Diagnostics.Stopwatch.GetElapsedTime(timestamp);
        }
    }

    internal StepEvidenceCollector(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        StepEvidenceBinding binding, ConfigurationWriterProcess writer, AttemptBusinessJudgement? judgement = null, StepJudgementWorker? worker = null, BindingRawAllowance? bindingAllowance = null, ProviderControlSession? provider = null,
        Func<BlobReference, ValueTask<BlobReference?>>? observeAdapter = null, AttemptWriterState? preparedState = null)
        : this(instance, database, contracts, lease, binding, writer, null, judgement, worker, bindingAllowance, provider, observeAdapter, preparedState) { }

    internal StepEvidenceCollector(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        StepEvidenceBinding binding, ConfigurationRunController controller, AttemptBusinessJudgement? judgement = null, StepJudgementWorker? worker = null, BindingRawAllowance? bindingAllowance = null, ProviderControlSession? provider = null,
        Func<BlobReference, ValueTask<BlobReference?>>? observeAdapter = null, AttemptWriterState? preparedState = null)
        : this(instance, database, contracts, lease, binding, null, controller, judgement, worker, bindingAllowance, provider, observeAdapter, preparedState) { }

    private StepEvidenceCollector(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        StepEvidenceBinding binding, ConfigurationWriterProcess? writer, ConfigurationRunController? controller,
        AttemptBusinessJudgement? judgement, StepJudgementWorker? worker, BindingRawAllowance? bindingAllowance,
        ProviderControlSession? provider, Func<BlobReference, ValueTask<BlobReference?>>? observeAdapter, AttemptWriterState? preparedState)
    {
        lease.VerifyHeld(instance); _binding = binding; _writer = writer; _controller = controller;
        var reader = new StepEvidenceReader(instance, database, contracts);
        var context = preparedState is null ? reader.Context(binding) : reader.ContextFromValidatedRead(binding,
            preparedState.Journal, preparedState.Attempt, preparedState.Total, preparedState.Latest);
        _factory = new(instance, database, contracts, lease); _terminal = new(instance, database, contracts, lease);
        _judgement = judgement ?? new(binding.AttemptId, reader.Judgement(context.Total, binding.StepId));
        _worker = worker; _provider = provider; _observeAdapter = observeAdapter;
        if (_judgement.AttemptId != binding.AttemptId || (_judgement.Mode == ExecutionJudgementMode.Script) != (worker is not null))
            throw new IOException("execution_judgement_owner_unbound");
        _limits = reader.Limits(context.Total, binding.StepId);
        _observeStall = true;
        _output = new(redirectInput: provider is not null);
        _raw = new(new(instance, contracts), binding, context.Job.FileLogBaseline, decodeLines: true,
            maximumRecordedBytes: bindingAllowance is null ? BindingOutputBudget.Remaining(instance, database, contracts, binding) : RawOutputCodec.MaximumRecordedBytes,
            bindingAllowance: bindingAllowance, live: lease.LiveLogs.Register(instance, database, contracts, binding),
            observeLine: line => _judgement.ObserveLine(_judgement.InputIdentity, line));
        _activity = new(_raw); _capture = new(_output, _activity);
        try { if (context.Job.FileLogBaseline is not null) _files = new(instance, database, contracts, binding, _raw, _activity); }
        catch
        {
            _output.CloseUnstartedEnds(); _raw.CompleteFiles("execution_log_capture_unavailable");
            _ = ReleaseCaptureAsync(); throw;
        }
        _worker?.Bind(_raw.SnapshotAsync, PublishScriptAsync);
        _pipeLines = DrainLinesAsync();
        _lines = ConsumeChangesAsync();
    }

    internal bool TryRequestStop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stopProof ??= StepStopProof.Capture(this);
        return _stopProof is not null;
    }

    internal T? UseStopObservation<T>(Func<StepEvidenceBinding, ProcessIdentity, StepStopObservation, T> callback) where T : class =>
        UseNative(null, (root, code) =>
        {
            if (root is null || code is not null) return null;
            var elapsed = _writer is not null ? _writer.ElapsedSinceStart : _controller!.ElapsedSinceStart;
            var activity = _activity.Read(elapsed);
            _worker?.Poll(_raw.JudgementBytes);
            var business = _business.Task.IsCompletedSuccessfully ? _business.Task.Result.Outcome : (BusinessOutcome?)null;
            var sinceBusiness = business.HasValue ? ElapsedSinceAcceptedBusiness : null;
            var cause = StepStopPolicy.Evaluate(_limits, elapsed, activity.Idle, business, sinceBusiness, _observeStall);
            if (cause == StepStopCause.Continue) return null;
            var milliseconds = elapsed.Ticks / TimeSpan.TicksPerMillisecond;
            var observation = new StepStopObservation(StepStopProof.Reason(cause), milliseconds,
                Math.Max(0, milliseconds - activity.Idle.Ticks / TimeSpan.TicksPerMillisecond), activity.Bytes,
                sinceBusiness is { } since ? Math.Max(0, milliseconds - since.Ticks / TimeSpan.TicksPerMillisecond) : null);
            return callback(_binding, root, observation);
        });

    internal OwnedWorkerIdentity Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) throw new IOException("execution_output_start_closed");
        _started = true;
        try
        {
            _worker?.Start();
            var result = _writer is not null ? _writer.Start(_output) : _controller!.Start(_output);
            _provider?.AfterStart(_output);
            _files?.Start(); return result;
        }
        catch { _output.CloseUnstartedEnds(); throw; }
    }

    internal async Task ObserveExitAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_files is not null) await _files.SampleAsync().ConfigureAwait(false);
            var raw = await _raw.SnapshotAsync().ConfigureAwait(false);
            Observe(raw);
        }
        finally { _gate.Release(); }
    }

    internal async Task<BlobReference> DrainOutputAsync(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started) _output.CloseUnstartedEnds();
        var drain = await _capture.DrainAsync(timeout).ConfigureAwait(false);
        if (drain.DeadlineExceeded) throw new IOException("execution_output_collection_pending");
        await _pipeLines.ConfigureAwait(false);
        if (_files is not null) await _files.SampleAsync().ConfigureAwait(false);
        return await _raw.SnapshotAsync().WaitAsync(timeout).ConfigureAwait(false);
    }

    internal async Task<PreparedStepResult> CollectAsync(StoppedConfigurationEditor stopped, TimeSpan timeout)
    {
        try
        {
            if (_provider is not null) _providerControl = await _provider.FinishAsync(stopped, timeout).ConfigureAwait(false);
            _ = await DrainOutputAsync(timeout).ConfigureAwait(false);
            if (_worker is not null) await _worker.FinalizeAsync().ConfigureAwait(false);
        }
        finally
        {
            Exception? stopFailure = null;
            try { if (_worker is not null) await _worker.StopAsync(timeout).ConfigureAwait(false); }
            catch (Exception error) { stopFailure = error; throw; }
            finally
            {
                try { if (_files is not null) await _files.FinishAsync().ConfigureAwait(false); }
                catch (Exception fileFailure) when (stopFailure is not null)
                { throw new IOException("execution_stop_and_file_finalization_failed", new AggregateException(stopFailure, fileFailure)); }
            }
        }
        var raw = await _raw.Completion.WaitAsync(timeout).ConfigureAwait(false);
        await _lines.ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_observeAdapter is not null) _adapterObservation ??= await _observeAdapter(raw).ConfigureAwait(false);
            var evidence = UseNative(stopped, (root, code) => _factory.CreateLive(_binding, raw, true, root, code, _observation, _stopProof, _worker?.Snapshot(),
                _providerControl, _adapterObservation, _provider?.WorkspaceOutput));
            var authority = _writer is not null ? StepEvidenceAuthority.Live(_factory, _binding, evidence, _writer, stopped) :
                StepEvidenceAuthority.Live(_factory, _binding, evidence, _controller!, stopped);
            return new(authority);
        }
        finally { _gate.Release(); }
    }

    private async Task ConsumeChangesAsync()
    {
        await foreach (var _ in _raw.Changes.ReadAllAsync().ConfigureAwait(false))
        {
            if (_worker is not null) { _worker.Poll(_raw.JudgementBytes); continue; }
            if (_judgement.Decision is null || _business.Task.IsCompletedSuccessfully) continue;
            await _gate.WaitAsync().ConfigureAwait(false);
            try { Observe(await _raw.SnapshotAsync().ConfigureAwait(false)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { ObservationFailureCode ??= "execution_observation_not_persisted"; }
            finally { _gate.Release(); }
        }
    }

    private async Task DrainLinesAsync()
    {
        await foreach (var _ in _capture.Lines.ReadAllAsync().ConfigureAwait(false)) { }
    }

    private async Task ReleaseCaptureAsync()
    {
        try { await _capture.Completion.ConfigureAwait(false); await _raw.Completion.ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { ObservationFailureCode ??= "execution_capture_cleanup_failed"; }
        finally { _raw.Dispose(); _output.Dispose(); _gate.Dispose(); }
    }

    private void Observe(BlobReference raw)
    {
        var evidence = UseNative(null, (root, code) => code is null && _judgement.Decision is null ? null :
            _factory.CreateLive(_binding, raw, false, root, code, _observation, judgementEvidence: _worker?.Snapshot()));
        if (evidence is null) return;
        if (evidence.Engine is EngineOutcome.Running or EngineOutcome.Unknown && evidence.Business == BusinessOutcome.Unverified ||
            _latest is not null && _latest.Engine == evidence.Engine && _latest.Business == evidence.Business) return;
        var authority = _writer is not null ? StepEvidenceAuthority.Live(_factory, _binding, evidence, _writer, null) :
            StepEvidenceAuthority.Live(_factory, _binding, evidence, _controller!, null);
        _terminal.RecordObservation(_binding.ParentJournalId, new(authority));
        _latest = evidence; _observation = evidence.Artifact;
        if (_judgement.Decision is { } decision && decision.Outcome == evidence.Business &&
            evidence.Business is BusinessOutcome.Succeeded or BusinessOutcome.Partial or BusinessOutcome.Failed)
        {
            Interlocked.CompareExchange(ref _businessTimestamp, System.Diagnostics.Stopwatch.GetTimestamp(), 0);
            _business.TrySetResult(decision);
        }
    }

    private async Task PublishScriptAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { Observe(await _raw.SnapshotAsync().ConfigureAwait(false)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { ObservationFailureCode ??= "execution_observation_not_persisted"; }
        finally { _gate.Release(); }
    }

    private T UseNative<T>(StoppedConfigurationEditor? stopped, Func<ProcessIdentity?, int?, T> callback) => _writer is not null ?
        _writer.UseStepEvidence(_binding, stopped, callback) : _controller!.UseStepEvidence(_binding, stopped, callback);

    public void Dispose()
    {
        if (_disposed) return;
        if (!_capture.Completion.IsCompleted || !_lines.IsCompleted || !_raw.Completion.IsCompleted)
            throw new IOException("execution_output_collection_pending");
        _disposed = true; _files?.Dispose(); _raw.Dispose(); _output.Dispose(); _gate.Dispose();
    }
}
