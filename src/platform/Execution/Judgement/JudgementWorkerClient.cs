using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Execution.Judgement;

internal sealed record JudgementWorkerExchange(JudgementWorkerInvocation Invocation, JudgementWorkerResponse Response,
    IReadOnlyList<ProcessIdentity> InterpreterChildren)
{
    internal IReadOnlyList<JudgementProbeExchange> Probes { get; init; } = [];
}

[SupportedOSPlatform("windows")]
internal sealed class JudgementWorkerClient
{
    private readonly OwnedProcessOutput _output;
    private readonly JudgementWorkerInitialization _initialization;
    private readonly Task _errors;
    private readonly Func<ProcessIdentity, CancellationToken, Task> _admitInterpreter;
    private readonly Func<Func<CancellationToken, Task>, CancellationToken, Task> _admitInvocation;
    private readonly Func<JudgementProbeRequest, CancellationToken, Task<JudgementProbeReply>> _observeProbe;
    private readonly List<JudgementProbeExchange> _probes = [];
    private long _probeBytes;
    internal IReadOnlyList<JudgementProbeExchange> Probes => _probes.ToArray();
    private long _generation;
    private int _busy;
    private bool _ready;
    private bool _final;
    private bool _faulted;

    internal JudgementWorkerClient(OwnedProcessOutput output, JudgementWorkerInitialization initialization,
        Func<ProcessIdentity, CancellationToken, Task> admitInterpreter,
        Func<Func<CancellationToken, Task>, CancellationToken, Task>? admitInvocation = null,
        Func<JudgementProbeRequest, CancellationToken, Task<JudgementProbeReply>>? observeProbe = null)
    {
        JudgementWorkerProtocol.Require(initialization);
        _output = output;
        _initialization = initialization;
        _admitInterpreter = admitInterpreter ?? throw new ArgumentNullException(nameof(admitInterpreter));
        _admitInvocation = admitInvocation ?? ((write, token) => write(token));
        _observeProbe = observeProbe ?? ((request, token) => JudgementNativeProbes.ObserveAsync(request, token));
        _errors = DrainErrorsAsync(output.StandardError);
    }

    internal async Task InitializeAsync(OwnedWorkerIdentity actualRoot, CancellationToken cancellationToken)
    {
        Enter();
        try
        {
            if (_ready || actualRoot.InstanceId != _initialization.Identity.Primary.InstanceId ||
                actualRoot.AttemptId != _initialization.Identity.Primary.AttemptId ||
                !string.Equals(actualRoot.ImagePath, _initialization.Broker.Path, StringComparison.OrdinalIgnoreCase) ||
                actualRoot.ImageSha256 != _initialization.Broker.Sha256)
                throw new IOException("judgement_worker_root_mismatch");
            await JudgementWorkerHost.WriteAsync(_output.StandardInput, _initialization, cancellationToken).ConfigureAwait(false);
            var ready = JudgementWorkerProtocol.Decode<JudgementWorkerReady>(await ReadAsync(cancellationToken).ConfigureAwait(false));
            var expected = new ProcessIdentity(actualRoot.ProcessId, actualRoot.CreatedAtUtc, actualRoot.ImagePath,
                actualRoot.ImageSha256, actualRoot.UserSid, actualRoot.IsElevated);
            if (ready.Format != JudgementWorkerProtocol.ReadyFormat || ready.Version != 1 ||
                ready.Identity != _initialization.Identity || ready.Process != expected)
                throw new IOException("judgement_worker_ready_mismatch");
            _ready = true;
        }
        catch { _faulted = true; throw; }
        finally { Volatile.Write(ref _busy, 0); }
    }

    internal async Task<JudgementWorkerExchange> InvokeAsync(JudgementWorkerInvocation invocation, CancellationToken cancellationToken)
    {
        Enter();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(_initialization.InvocationTimeoutSeconds));
        var cancellation = deadline.Token;
        try
        {
            if (!_ready || _final || _generation >= _initialization.MaximumInvocations)
                throw new IOException("judgement_worker_invocation_unavailable");
            JudgementWorkerProtocol.Require(invocation, _initialization.Identity, checked(_generation + 1));
            _probes.Clear(); _probeBytes = 0;
            _generation = invocation.Generation;
            _final = invocation.IsFinal;
            await _admitInvocation(token => JudgementWorkerHost.WriteAsync(_output.StandardInput, invocation, token), cancellation).ConfigureAwait(false);
            var children = new List<ProcessIdentity>(1);
            while (true)
            {
                var bytes = await ReadAsync(cancellation).ConfigureAwait(false);
                using var document = Serialization.StrictJson.Parse(bytes);
                var format = document.RootElement.TryGetProperty("format", out var field) ? field.GetString() : null;
                if (format == JudgementWorkerProtocol.ChildReadyFormat)
                {
                    if (_initialization.Language != "python" || children.Count != 0 || _probes.Count != 0)
                        throw new IOException("judgement_python_child_out_of_order");
                    var child = JudgementWorkerProtocol.Decode<WorkerChildReady>(bytes);
                    RequireChild(child, invocation);
                    await _admitInterpreter(child.Process, cancellation).ConfigureAwait(false);
                    children.Add(child.Process);
                    await _admitInvocation(token => JudgementWorkerHost.WriteAsync(_output.StandardInput,
                        new WorkerChildAdmission(JudgementWorkerProtocol.ChildAdmissionFormat, 1,
                            invocation.Identity, invocation.Generation, child.Process), token), cancellation).ConfigureAwait(false);
                    continue;
                }
                if (format == JudgementProbeProtocol.RequestFormat)
                {
                    if (_initialization.Language == "python" && children.Count != 1)
                        throw new IOException("judgement_python_child_not_admitted");
                    await ObserveProbeAsync(JudgementWorkerProtocol.Decode<JudgementProbeRequest>(bytes), invocation, cancellation).ConfigureAwait(false);
                    continue;
                }
                var response = JudgementWorkerProtocol.Decode<JudgementWorkerResponse>(bytes);
                JudgementWorkerProtocol.Require(response, invocation);
                if (response.ProbeCount != _probes.Count || response.ProbeTranscriptSha256 != JudgementProbeProtocol.Hash(_probes))
                    throw new IOException("judgement_probe_transcript_mismatch");
                if (_initialization.Language == "python" && response.Result is not null && children.Count != 1)
                    throw new IOException("judgement_python_child_not_admitted");
                return new(invocation, response, children.ToArray()) { Probes = Probes };
            }
        }
        catch { _faulted = true; throw; }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private async Task ObserveProbeAsync(JudgementProbeRequest request, JudgementWorkerInvocation invocation, CancellationToken cancellation)
    {
        JudgementProbeProtocol.Require(request, invocation, _probes.Count + 1);
        await _admitInvocation(_ => Task.CompletedTask, cancellation).ConfigureAwait(false);
        var reply = await _observeProbe(request, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        JudgementProbeProtocol.Require(reply, request);
        var exchange = new JudgementProbeExchange(request, reply);
        var size = JudgementWorkerProtocol.Encode(exchange).LongLength;
        var remainingFaults = (JudgementProbeProtocol.MaximumCalls - _probes.Count - 1L) * JudgementProbeProtocol.FaultExchangeBytes;
        if (size > invocation.ProbeEvidenceAllowance - remainingFaults - _probeBytes)
        {
            reply = JudgementProbeProtocol.Fault(request, "judgement_probe_budget_exceeded");
            exchange = new(request, reply); size = JudgementWorkerProtocol.Encode(exchange).LongLength;
        }
        if (size > invocation.ProbeEvidenceAllowance - _probeBytes) throw new IOException("judgement_probe_budget_exceeded");
        _probeBytes = checked(_probeBytes + size); _probes.Add(exchange);
        await _admitInvocation(token => JudgementWorkerHost.WriteAsync(_output.StandardInput, reply, token), cancellation).ConfigureAwait(false);
    }

    internal async Task CompleteAsync(CancellationToken cancellationToken)
    {
        Enter();
        try
        {
            _output.CompleteInput();
            if (await JudgementWorkerFraming.ReadAsync(_output.StandardOutput, cancellationToken).ConfigureAwait(false) is not null)
                throw new IOException("judgement_worker_extra_output");
            await _errors.ConfigureAwait(false);
        }
        finally { _faulted = true; Volatile.Write(ref _busy, 0); }
    }

    internal void RequireHealthy()
    {
        if (_faulted) throw new IOException("judgement_worker_transport_faulted");
        if (_errors.IsCompleted) _errors.GetAwaiter().GetResult();
    }

    internal async Task DrainAfterStopAsync()
    {
        if (Volatile.Read(ref _busy) != 0) throw new InvalidOperationException("judgement_worker_transport_busy");
        await _errors.ConfigureAwait(false);
    }

    private void RequireChild(WorkerChildReady child, JudgementWorkerInvocation invocation)
    {
        var image = _initialization.Python;
        if (image is null || child.Format != JudgementWorkerProtocol.ChildReadyFormat || child.Version != 1 ||
            child.Identity != invocation.Identity || child.Generation != invocation.Generation || child.Process is not { } process ||
            process.ProcessId <= 0 || process.CreatedAtUtc.Kind != DateTimeKind.Utc ||
            !string.Equals(process.ImagePath, image.Path, StringComparison.OrdinalIgnoreCase) || process.ImageSha256 != image.Sha256)
            throw new IOException("judgement_python_child_mismatch");
    }

    private async Task<byte[]> ReadAsync(CancellationToken cancellationToken)
    {
        RequireHealthy();
        var bytes = await JudgementWorkerFraming.ReadAsync(_output.StandardOutput, cancellationToken).ConfigureAwait(false);
        RequireHealthy();
        return bytes ?? throw new IOException("judgement_worker_output_incomplete");
    }

    private void Enter()
    {
        RequireHealthy();
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw new InvalidOperationException("judgement_worker_transport_busy");
    }

    private static async Task DrainErrorsAsync(Stream input)
    {
        var buffer = new byte[4096];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            total = checked(total + count);
            if (total > 20 * 1024 * 1024) throw new IOException("judgement_worker_error_output_exceeded");
        }
    }
}
