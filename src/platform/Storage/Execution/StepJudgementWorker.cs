using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class StepJudgementWorker : IDisposable
{
    private readonly object _gate = new();
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly InstanceLease _lease;
    private readonly ExecutionPlan _total;
    private readonly StepEvidenceBinding _primary;
    private readonly AttemptBusinessJudgement _owner;
    private readonly Func<bool> _stopRequested;
    private readonly ExecutionProviderRegistry.PreparedExecutionProvider? _provider;
    private readonly ImmutableBlobStore _blobs;
    private readonly List<ExecutionJudgementExchange> _exchanges = [];
    private readonly List<IDisposable> _held = [];
    private readonly CancellationTokenSource _transportStopping = new();
    private ConfigurationWriterProcess? _writer;
    private StepWriterScope? _scope;
    private OwnedProcessOutput? _output;
    private JudgementWorkerClient? _client;
    private JudgementWorkerInitialization? _initialization;
    private JudgementInterpreterLease? _inputs;
    private StoppedConfigurationEditor? _stopped;
    private Task _ready = Task.CompletedTask, _pending = Task.CompletedTask;
    private Func<Task<BlobReference>>? _capture;
    private Func<Task>? _publish;
    private bool _started, _closing, _faulted, _released;
    private long _lastBytes, _lastInvocation, _recordedBytes;
    private BlobReference? _periodicRejectedRaw;
    private readonly Func<JudgementProbeRequest, CancellationToken, Task<JudgementProbeReply>>? _observeProbe;

    internal StepJudgementWorker(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        ExecutionPlan total, StepEvidenceBinding primary, AttemptBusinessJudgement owner, Func<bool> stopRequested,
        Func<JudgementProbeRequest, CancellationToken, Task<JudgementProbeReply>>? observeProbe = null,
        ExecutionProviderRegistry.PreparedExecutionProvider? provider = null)
    {
        _instance = instance; _database = database; _contracts = contracts; _lease = lease; _total = total;
        _primary = primary; _owner = owner; _stopRequested = stopRequested; _provider = provider; _observeProbe = observeProbe; _blobs = new(instance, contracts);
    }

    internal void Prepare()
    {
        _scope = new(_instance, _database, _contracts, _lease, _primary.ParentJournalId, _primary.StepId, "judgement", _owner.InputIdentity.OwnerId, _provider);
        _writer = new(_instance, _lease, _scope, deferRegistration: true);
        _output = new(redirectInput: true);
        _initialization = new ExecutionJudgementInputs(_instance, _contracts).ReadInitialization(_total, _primary.StepId,
            _primary, _writer.JobName, _owner.InputIdentity.OwnerId);
        _inputs = new(_initialization);
        _client = new(_output, _initialization, (child, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested();
            if (_stopRequested()) throw new IOException("judgement_python_child_admission_closed");
            _writer.RequireInterpreterChild(child); return Task.CompletedTask;
        }, (write, cancellation) => _writer.AdmitJudgementWrite(write, cancellation, _output,
            TimeSpan.FromSeconds(_total.Budget.JudgementTimeoutSeconds)), _observeProbe);
        _writer.RegisterPreparedJob();
    }

    internal void Bind(Func<Task<BlobReference>> capture, Func<Task> publish)
    { _capture = capture; _publish = publish; }

    internal Task WhenIdle { get { lock (_gate) return _pending; } }

    internal void Start()
    {
        if (_started || _writer is null || _client is null) throw new IOException("execution_judgement_start_unavailable");
        _started = true;
        var root = _writer.Start(_output);
        _ready = Task.Run(() => _client.InitializeAsync(root, CancellationToken.None));
    }

    internal void Poll(long observedBytes)
    {
        lock (_gate)
        {
            if (!_started || _closing || _faulted || observedBytes == 0 || _capture is null ||
                _initialization is null || _exchanges.Count >= _initialization.MaximumInvocations - 1 || !_pending.IsCompleted) return;
            if (observedBytes == _lastBytes && (_lastInvocation == 0 || Stopwatch.GetElapsedTime(_lastInvocation) < TimeSpan.FromSeconds(30))) return;
            if (_owner.Decision is not null || _stopRequested()) return;
            _lastBytes = observedBytes; _lastInvocation = Stopwatch.GetTimestamp();
            _pending = Task.Run(() => InvokeAsync(final: false));
        }
    }

    private async Task InvokeAsync(bool final)
    {
        JudgementInvocation? accepted = null;
        BlobReference? invocationReference = null, raw = null;
        JudgementWorkerInvocation? invocation = null;
        var probes = new List<BlobReference>();
        var probeArchivingAttempted = false;
        long probeAllowance = 0;
        try
        {
            await _ready.ConfigureAwait(false);
            if (_stopRequested()) { _owner.Cancel(); return; }
            raw = await _capture!().ConfigureAwait(false);
            if (!final && IsPeriodicallyRejected(raw)) return;
            var draft = new JudgementInputBuilder(_instance, _database, _contracts).Prepare(_total, _initialization!.Identity, raw, final);
            lock (_gate)
            {
                if (!final && _closing || _owner.IsCancelled || _stopRequested() ||
                    _exchanges.Count >= _initialization.MaximumInvocations - (final ? 0 : 1)) return;
                var available = JudgementEvidenceBudget.MaximumInputBytes(_initialization.MaximumEvidenceBytes, _recordedBytes, final);
                probeAllowance = JudgementEvidenceBudget.AllocateProbes(_initialization.MaximumEvidenceBytes, _recordedBytes, draft.MaximumEncodedBytes, final);
                var transcript = new ExecutionJudgementEvidence(JudgementEvidenceCodec.Format, 1, _initialization.Identity,
                    _initialization.Language, _initialization.ScriptSha256, _exchanges.ToImmutableArray(), null);
                if (probeAllowance == 0 || draft.MaximumEncodedBytes > available || !JudgementEvidenceCodec.CanAppend(transcript, final))
                {
                    if (!final && draft.StableMinimumEncodedBytes > available) _periodicRejectedRaw = raw;
                    return;
                }
                accepted = final ? _owner.TryBeginFinal() : _owner.TryBeginPeriodic();
            }
            if (accepted is null) return;
            invocation = draft.Bind(accepted) with { ProbeEvidenceAllowance = probeAllowance };
            var size = JudgementWorkerProtocol.Encode(invocation).LongLength;
            lock (_gate)
            {
                if (size > _initialization.MaximumEvidenceBytes - _recordedBytes) throw new IOException("judgement_evidence_capacity_exhausted");
                _recordedBytes += size;
            }
            invocationReference = JudgementInvocationArchive.Write(_blobs, invocation, Hold);
            var exchange = await _client!.InvokeAsync(invocation, _transportStopping.Token).ConfigureAwait(false);
            probeArchivingAttempted = true;
            ArchiveProbes(invocation, exchange.Probes, probes);
            var responseBytes = JudgementWorkerProtocol.Encode(exchange.Response);
            lock (_gate)
            {
                if (responseBytes.LongLength > _initialization.MaximumEvidenceBytes - _recordedBytes)
                    throw new IOException("judgement_evidence_capacity_exhausted");
                _recordedBytes += responseBytes.LongLength;
            }
            var response = _blobs.PersistPlan(responseBytes); Hold(response);
            lock (_gate) _exchanges.Add(new(raw, invocationReference, response, null) { InterpreterChildren = exchange.InterpreterChildren.ToImmutableArray(), ProbeObservations = probes.ToImmutableArray() });
            if (_stopRequested()) _owner.Cancel();
            if (exchange.Response.Result is { } result) _owner.Complete(accepted, result);
            else _owner.CompleteFault(accepted);
            if (_publish is not null) await _publish().ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            if (!probeArchivingAttempted && invocation is not null && _client is not null)
            {
                probeArchivingAttempted = true;
                try { ArchiveProbes(invocation, _client.Probes, probes); }
                catch (Exception archiveError) when (archiveError is IOException or System.Text.Json.JsonException or InvalidOperationException) { }
            }
            lock (_gate)
            {
                _faulted = true;
                if (invocationReference is not null && raw is not null) _exchanges.Add(new(raw, invocationReference, null, "judgement_worker_transport_faulted") { ProbeObservations = probes.ToImmutableArray() });
            }
            if (_stopRequested()) _owner.Cancel();
            if (accepted is not null) _owner.CompleteFault(accepted);
        }
    }

    private void ArchiveProbes(JudgementWorkerInvocation invocation, IReadOnlyList<JudgementProbeExchange> exchanges, List<BlobReference> references)
    {
        JudgementProbeProtocol.RequireExchanges(exchanges, invocation);
        foreach (var exchange in exchanges)
        {
            var bytes = JudgementWorkerProtocol.Encode(exchange);
            lock (_gate)
            {
                if (bytes.LongLength > _initialization!.MaximumEvidenceBytes - _recordedBytes)
                    throw new IOException("judgement_evidence_capacity_exhausted");
                _recordedBytes += bytes.LongLength;
            }
            var reference = JudgementProbeArchive.Write(_blobs, exchange, Hold); references.Add(reference);
        }
    }

    private bool IsPeriodicallyRejected(BlobReference raw)
    {
        lock (_gate)
        {
            if (_periodicRejectedRaw != raw) { _periodicRejectedRaw = null; return false; }
        }
        _lease.VerifyHeld(_instance);
        // The rejection concerns immutable log bytes, never a prior files/locale sampling.
        var manifest = RawOutputCodec.Read(_blobs, _primary, raw);
        var held = new List<IDisposable>();
        try
        {
            held.Add(_blobs.OpenVerified(raw));
            foreach (var content in manifest.Parts.Select(part => part.Content).Distinct()) held.Add(_blobs.OpenVerified(content));
            return true;
        }
        finally { foreach (var handle in held) handle.Dispose(); }
    }

    internal async Task FinalizeAsync()
    {
        lock (_gate) _closing = true;
        if (!_started || _owner.Decision is not null || _faulted || _owner.IsCancelled) return;
        while (!_pending.IsCompleted)
        {
            if (_stopRequested()) { _owner.Cancel(); return; }
            await Task.WhenAny(_pending, Task.Delay(50)).ConfigureAwait(false);
        }
        if (_stopRequested()) { _owner.Cancel(); return; }
        _owner.RequestFinal();
        lock (_gate) _pending = Task.Run(() => InvokeAsync(final: true));
        while (!_pending.IsCompleted)
        {
            if (_stopRequested()) { _owner.Cancel(); return; }
            await Task.WhenAny(_pending, Task.Delay(50)).ConfigureAwait(false);
        }
    }

    internal async Task StopAsync(TimeSpan timeout)
    {
        lock (_gate) _closing = true;
        if (_writer is null || _released) return;
        var registered = _lease.PublishExecution(() => _scope!.VerifyReadOnly().Jobs.Any(job => job.JobName == _writer.JobName));
        if (!registered)
        {
            _writer.ReleaseEmptyJob(); _released = true; _output?.CloseUnstartedEnds();
        }
        else
        {
            if (!_started) _output?.CloseUnstartedEnds();
            _stopped = await _writer.StopAsync(timeout).ConfigureAwait(false);
        }
        await _transportStopping.CancelAsync().ConfigureAwait(false);
        // Stop the real family before waiting for anonymous pipe reads to leave the transport.
        try { await _ready.WaitAsync(timeout).ConfigureAwait(false); } catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException) { _faulted = true; }
        await _pending.WaitAsync(timeout).ConfigureAwait(false);
        if (_client is not null)
            try { await _client.DrainAfterStopAsync().WaitAsync(timeout).ConfigureAwait(false); }
            catch (IOException) { _faulted = true; }
    }

    internal BlobReference? Snapshot()
    {
        if (_writer is null || _initialization is null || _released) return null;
        return _writer.UseJudgementEvidence(_primary, _stopped, _ =>
        {
            lock (_gate)
            {
                var value = new ExecutionJudgementEvidence(JudgementEvidenceCodec.Format, 1, _initialization.Identity,
                    _initialization.Language, _initialization.ScriptSha256, _exchanges.ToImmutableArray(), _stopped?.Reference);
                var reference = JudgementEvidenceCodec.Write(_blobs, value); Hold(reference); return reference;
            }
        });
    }

    private void Hold(BlobReference reference)
    {
        var stream = _blobs.OpenVerified(reference);
        lock (_gate) _held.Add(stream);
    }

    public void Dispose()
    {
        if (_writer is not null && _stopped is null && !_released || !_pending.IsCompleted) throw new IOException("execution_judgement_collection_pending");
        _writer?.Dispose(); _output?.Dispose(); _inputs?.Dispose();
        foreach (var held in _held) held.Dispose();
        _transportStopping.Dispose();
    }
}
