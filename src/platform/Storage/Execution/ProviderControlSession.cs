using System.Collections.Immutable;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class ProviderControlSession : IDisposable
{
    private readonly object _gate = new();
    private readonly InstanceLease _lease;
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly ImmutableBlobStore _blobs;
    private readonly StepEvidenceBinding _binding;
    private readonly ExecutionProviderRegistry.PreparedExecutionProvider _authority;
    private readonly ExecutionTimeBudget _budget;
    private readonly ConfigurationWriterProcess? _writer;
    private readonly ConfigurationRunController? _controller;
    private readonly ExecutionProviderSession _identity;
    private readonly ProviderProtocolState _protocol;
    private readonly NamedPipeServerStream _pipe;
    private readonly BlobReference _bootstrap;
    private readonly byte[] _bootstrapBytes;
    private readonly MemoryStream _frames = new();
    private readonly List<IDisposable> _held = [];
    private readonly CancellationTokenSource _readDeadline = new();
    private readonly Task _receive;
    private readonly ProviderProjectServer? _project;
    private readonly ProviderDeviceServer? _device;
    private readonly ProviderWorkspace? _workspace;
    private ProviderDeviceSession? _deviceSession;
    private readonly TaskCompletionSource<ExecutionProviderDecision> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ProcessIdentity? _peer;
    private OwnedProcessOutput? _output;
    private BlobReference? _artifact, _faultFrame;
    private string? _fault;
    private bool _started, _disposed;
    private bool _acknowledged, _stopSent;
    internal Task<ExecutionProviderDecision> Terminal => _decision.Task;
    internal bool HasTerminal { get { lock (_gate) return _protocol.Decision is not null; } }
    internal BlobReference? WorkspaceOutput { get; private set; }

    internal ProviderControlSession(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        StepEvidenceBinding binding, ExecutionProviderRegistry.PreparedExecutionProvider authority,
        ExecutionTimeBudget budget, ConfigurationWriterProcess? writer, ConfigurationRunController? controller, AttemptWriterState? preparedState = null)
    {
        if ((writer is null) == (controller is null)) throw new IOException("execution_provider_native_owner_required");
        lease.VerifyHeld(instance);
        _instance = instance; _database = database; _contracts = contracts; _lease = lease; _binding = binding;
        _authority = authority; _budget = budget; _writer = writer; _controller = controller; _blobs = new(instance, contracts);
        var reader = new StepEvidenceReader(instance, database, contracts);
        var context = preparedState is null ? reader.Context(binding) : reader.ContextFromValidatedRead(binding,
            preparedState.Journal, preparedState.Attempt, preparedState.Total, preparedState.Latest);
        var step = context.Total.Steps.Single(step => step.StepId == binding.StepId);
        var provider = step.Provider ?? throw new IOException("execution_provider_missing");
        _identity = ProviderControlReader.Identity(binding, step, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
        _protocol = new(_identity);
        _authority.Use(package =>
        {
            if (authority.ActivationId != Guid.Parse(provider.ActivationId) || authority.ExtensionId != provider.ExtensionId ||
                package.Declaration.EntryId != provider.EntryId) throw new IOException("execution_provider_activation_unbound");
            return true;
        });
        var ordinal = preparedState is null ? ProviderControlInputs.Ordinal(instance, database, contracts, binding) : checked((int)preparedState.Attempt.AttemptOrdinal);
        try
        {
            if (step.Adapter?.Compiled is not null) _workspace = new(instance, lease, _identity);
            var bootstrap = ProviderControlInputs.Build(step, _identity, ordinal) with { Workspace = _workspace?.Grant };
            ProviderControlReader.RequireBootstrap(instance, context.Total, binding, _identity, bootstrap, ordinal);
            _bootstrapBytes = ExecutionProviderFrames.Encode(bootstrap, ExecutionProviderFrames.MaximumBootstrapBytes);
            _bootstrap = _blobs.PersistPlan(_bootstrapBytes); Hold(_bootstrap);
            _pipe = new NamedPipeServerStream(bootstrap.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var remaining = budget.Read().Remaining;
            if (remaining == TimeSpan.Zero) _readDeadline.Cancel(); else _readDeadline.CancelAfter(remaining);
            if (bootstrap.Project is { } project)
            {
                var reference = step.Adapter!.Compiled!;
                var snapshot = CompiledProjectCodec.Read(instance, _blobs, new(reference.RelativePath, reference.SizeBytes, reference.Sha256));
                if (!snapshot.Value.CanExecute || snapshot.Value.Consumer.Installed.ExtensionId != step.ExtensionId ||
                    snapshot.Value.BindingId.ToString("D") != step.BindingId) throw new IOException("execution_project_unbound");
                var retry = TaskRetryEvidence.ForJob(instance, contracts, context.Total, binding);
                var selectedIds = TaskRetryEvidence.InvocationIds(step, retry);
                var selected = selectedIds.Select(id => step.Tasks.Single(task => task.TaskId == id)).ToArray();
                var executionIds = selected.Any(task => task.ExecutionTaskId is not null) ? selected.Select(task => task.ExecutionTaskId ?? task.TaskId).ToArray() : null;
                _project = new(_blobs, snapshot, project.PipeName, contracts, RequireProjectPeer, _readDeadline.Token, selectedIds, executionIds,
                    database, RequireProtectedPeer, TaskRetryEvidence.RepetitionOffsets(_blobs, step, retry));
            }
            if (bootstrap.Device is { } device)
                _device = new(device.PipeName, contracts, RequireProjectPeer,
                    (request, token) => (_deviceSession ?? throw new IOException("execution_device_target_unbound")).InvokeAsync(request, token), _readDeadline.Token);
            _receive = ReceiveAsync();
        }
        catch
        {
            _readDeadline.Cancel(); _pipe?.Dispose();
            if (_project is not null) { _project.FinishAsync().GetAwaiter().GetResult(); _project.Dispose(); }
            if (_device is not null) { _device.CloseAsync().GetAwaiter().GetResult(); _device.Dispose(); }
            _workspace?.Dispose();
            _readDeadline.Dispose(); foreach (var item in _held) item.Dispose(); throw;
        }
    }

    internal void AfterStart(OwnedProcessOutput output)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) throw new IOException("execution_provider_bootstrap_closed");
            _started = true; _output = output;
        }
        _authority.Use(_ =>
        {
            var timeout = _budget.Limit(TimeSpan.FromSeconds(10));
            if (timeout == TimeSpan.Zero) throw new IOException("execution_provider_channel_deadline");
            Task Write(CancellationToken cancellation) => ExecutionProviderFrames.WriteAsync(output.StandardInput, _bootstrapBytes,
                ExecutionProviderFrames.MaximumBootstrapBytes, cancellation);
            AdmitWrite(output, Write, timeout);
            return true;
        });
    }

    internal void BindDevice(PcTargetStage? pc, AndroidTargetStage? android, StepLiveMedia? media, Func<bool> expired)
    {
        if (_device is null) return;
        lock (_gate)
        {
            if (_started || _deviceSession is not null || media is null) throw new IOException("execution_device_target_unbound");
            _deviceSession = new(pc, media, expired,
                android is null ? null : new AndroidDeviceInput(android, _binding, UseDeviceParent, _budget, expired));
        }
    }

    private void UseDeviceParent(Action<OwnedWorkerJob, OwnedWorkerIdentity> action)
    {
        bool Invoke(OwnedWorkerJob parent, OwnedWorkerIdentity primary) { action(parent, primary); return true; }
        _ = _writer is not null ? _writer.UsePrimaryParent(_binding, Invoke) : _controller!.UsePrimaryParent(_binding, Invoke);
    }

    internal async Task CloseDeviceAsync()
    {
        try { if (_device is not null) await _device.CloseAsync().ConfigureAwait(false); }
        finally { _deviceSession?.Close(); }
    }

    private async Task ReceiveAsync()
    {
        byte[]? pending = null;
        try
        {
            await _pipe.WaitForConnectionAsync(_readDeadline.Token).ConfigureAwait(false);
            UseNative(null, (root, code) =>
            {
                if (root is null || code is not null || !GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var pid) || pid != root.ProcessId)
                    throw new IOException("execution_provider_peer_unbound");
                lock (_gate) _peer = root;
                return true;
            });
            OwnedProcessOutput output;
            lock (_gate) output = _output ?? throw new IOException("execution_provider_admission_closed");
            var acknowledgementTimeout = _budget.Limit(TimeSpan.FromSeconds(10));
            if (acknowledgementTimeout == TimeSpan.Zero) throw new IOException("execution_provider_channel_deadline");
            AdmitWrite(output, cancellation => ExecutionProviderFrames.WriteAsync(_pipe, new byte[] { 1 }, 1, cancellation), acknowledgementTimeout);
            lock (_gate) _acknowledged = true;
            while (true)
            {
                pending = await ExecutionProviderFrames.ReadAsync(_pipe, ExecutionProviderFrames.MaximumEventBytes, _readDeadline.Token).ConfigureAwait(false);
                var frame = _contracts.Read<ExecutionProviderEvent>(pending, "execution-provider-event.schema.json");
                ExecutionProviderDecision? terminal;
                lock (_gate)
                {
                    if (_disposed || !_protocol.Observe(ProviderControlReader.Signal(frame), checked(pending.Length + 4)))
                        throw new IOException("execution_provider_admission_closed");
                    var prefix = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(prefix, pending.Length);
                    _frames.Write(prefix); _frames.Write(pending); pending = null;
                    terminal = _protocol.Decision;
                    if (terminal is not null) _artifact = PersistLocked();
                }
                if (terminal is null) continue;
                var proof = PreparedProviderTerminal.FromNative(this);
                try { new ProviderTerminalStore(_instance, _database, _contracts, _lease).Record(proof); }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // Collection reconciles the same immutable intent; a failed SQL projection is not a channel fault.
                    return;
                }
                _decision.TrySetResult(terminal); return;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or
            ObjectDisposedException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        {
            lock (_gate)
            {
                _fault ??= error is OperationCanceledException ? "execution_provider_channel_deadline" :
                    error.Message == "execution_provider_admission_closed" ? "execution_provider_admission_closed" :
                    pending is not null ? "execution_provider_protocol_invalid" : "execution_provider_channel_incomplete";
                if (pending is not null) { _faultFrame = _blobs.PersistContent(pending, default); Hold(_faultFrame); }
            }
        }
    }

    internal T UseTerminal<T>(Func<StepEvidenceBinding, BlobReference, ExecutionProviderDecision, T> effect) =>
        UseNative(null, (root, _) =>
        {
            _lease.VerifyHeld(_instance);
            BlobReference artifact; ExecutionProviderDecision decision;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_started || root is null || root != _peer || _artifact is null || _protocol.Decision is null)
                    throw new IOException("execution_provider_terminal_unbound");
                artifact = _artifact; decision = _protocol.Decision;
            }
            return effect(_binding, artifact, decision);
        });

    internal async Task<BlobReference?> FinishAsync(StoppedConfigurationEditor stopped, TimeSpan timeout)
    {
        await CloseDeviceAsync().ConfigureAwait(false);
        if (_project is not null) await _project.FinishAsync().ConfigureAwait(false);
        var hasRoot = UseNative(stopped, (root, _) => root is not null);
        if (!hasRoot) { _readDeadline.Cancel(); _pipe.Dispose(); }
        try { await _receive.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            _readDeadline.Cancel(); _pipe.Dispose();
            await _receive.ConfigureAwait(false);
        }
        bool hasTerminal;
        lock (_gate) hasTerminal = _protocol.Decision is not null && _artifact is not null;
        if (hasTerminal)
        {
            var proof = PreparedProviderTerminal.FromNative(this);
            try
            {
                new ProviderTerminalStore(_instance, _database, _contracts, _lease).Record(proof);
                _decision.TrySetResult(proof.Decision);
            }
            catch (IOException error) when (error.Message == "execution_provider_admission_closed") { }
        }
        BlobReference? control;
        lock (_gate)
        {
            if (_peer is null) return null;
            control = _artifact ??= PersistLocked();
        }
        if (_workspace is not null && WorkspaceOutput is null)
        {
            using var deadline = new CancellationTokenSource(timeout);
            WorkspaceOutput = UseNative(stopped, (root, _) => root is null ? throw new IOException("execution_provider_output_root_missing") :
                ProviderOutputCodec.Capture(_instance, _database, _contracts, _binding, control, Hold, deadline.Token));
        }
        return control;
    }

    internal async Task<bool> RequestStopAsync(TimeSpan timeout)
    {
        bool live = UseNative(null, (root, code) =>
        {
            lock (_gate) return !_disposed && _started && _acknowledged && root is not null && root == _peer && code is null;
        });
        if (!live || timeout <= TimeSpan.Zero) return false;
        lock (_gate)
        {
            if (_stopSent) return true;
            _stopSent = true;
        }
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            await ExecutionProviderFrames.WriteAsync(_pipe, new byte[] { ExecutionProviderFrames.StopRequest }, 1, deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { return false; }
    }

    private BlobReference PersistLocked()
    {
        var bytes = _frames.ToArray();
        var parts = bytes.Length == 0 ? ImmutableArray<BlobReference>.Empty :
            JudgementFrameParts.Write(_blobs, bytes, ProviderProtocolState.MaximumEncodedBytes, Hold);
        var document = new ProviderControlArchive(ProviderControlReader.Format, 1, _binding, _identity,
            _bootstrap, _peer ?? throw new IOException("execution_provider_peer_unbound"), bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), parts, _fault, _faultFrame);
        var artifact = _blobs.PersistPlan(BoundedJson.Serialize(document, StepResultCodec.Options)); Hold(artifact); return artifact;
    }

    private void AdmitWrite(OwnedProcessOutput output, Func<CancellationToken, Task> write, TimeSpan timeout)
    {
        if (_writer is not null) _writer.AdmitProviderBootstrap(_binding, output, write, timeout);
        else _controller!.AdmitProviderBootstrap(_binding, output, write, timeout);
    }

    private void Hold(BlobReference reference) => _held.Add(_blobs.OpenVerified(reference));

    private void RequireProjectPeer(uint pid) => _authority.Use(_ => UseNative(null, (root, code) =>
    {
        _lease.VerifyHeld(_instance);
        lock (_gate)
        {
            if (_disposed || !_started || _protocol.Decision is not null || root is null || code is not null || pid != root.ProcessId)
                throw new IOException("execution_project_peer_unbound");
        }
        return true;
    }));

    private void RequireProtectedPeer(uint pid)
    {
        RequireProjectPeer(pid);
        lock (_gate)
            if (_stopSent || _readDeadline.IsCancellationRequested) throw new IOException("execution_project_secret_admission_closed");
    }

    private T UseNative<T>(StoppedConfigurationEditor? stopped, Func<ProcessIdentity?, int?, T> action) =>
        _writer is not null ? _writer.UseStepEvidence(_binding, stopped, action) : _controller!.UseStepEvidence(_binding, stopped, action);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!_receive.IsCompleted) throw new IOException("execution_provider_collection_pending");
            _disposed = true; _protocol.CloseAdmission(); _pipe.Dispose(); _readDeadline.Dispose(); _frames.Dispose();
            _project?.Dispose();
            _device?.Dispose();
            _workspace?.Dispose();
            for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}
