using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class AndroidControlProcess : IDisposable
{
    private readonly object _gate = new();
    private readonly AndroidControlStore _store;
    private readonly OwnedWorkerJob _job = new();
    private readonly OwnedProcessOutput _output = new();
    private readonly TargetCommandOutput _raw;
    private readonly ProcessOutputCapture _capture;
    private readonly Task _lines;
    private readonly AndroidObservationAllowance _identities;
    private readonly List<IDisposable> _holds = [];
    private PreparedAndroidAuthority? _authority;
    private OwnedWorkerIdentity? _started;
    private PreparedStoppedTarget? _stopped;
    private PreparedAndroidObservation? _observation;
    private PreparedAndroidCollection? _collection;
    private PreparedAndroidRetention? _retention;
    private PreparedRetentionIntent? _retentionIntent;
    private OwnedTargetHandoff? _handoff;
    internal BlobReference? Retention { get; private set; }
    internal bool IsEmulatorLaunch => _authority is not null && _store.IsEmulatorLaunch(Request);
    private long? _startedTimestamp, _elapsedMilliseconds;
    private bool _attempted, _closing, _disposed, _releasedEmpty;
    private Task? _stopping;
    internal string JobName => _job.Name;
    internal bool WasReleasedUnregistered { get { lock (_gate) return _releasedEmpty; } }
    internal AndroidCommandRequest Request => Authority().Request;

    internal AndroidControlProcess(AndroidControlStore store, BindingRawAllowance raw,
        AndroidObservationAllowance identities)
    {
        _store = store; _identities = identities;
        try
        {
            _job.ObserveProcessIdentities(identities.Observe, identities.Reserve);
            _raw = new(raw); _capture = new(_output, _raw); _lines = DiscardLinesAsync();
        }
        catch { _job.Dispose(); _output.Dispose(); throw; }
    }

    internal void Register(int sequence, AndroidCommandPurpose purpose, ExecutionAndroidAction action,
        BlobReference? previousObservation)
    {
        lock (_gate)
        {
            RequireOpen();
            if (_authority is not null) throw new IOException("execution_android_command_already_registered");
            _authority = _store.Register(this, _job, sequence, purpose, action, previousObservation);
        }
    }

    internal bool TryResolveRegistration()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _authority ??= _store.ResolveRegistered(this, _job); return _authority is not null;
        }
    }

    internal async Task StartAsync(CancellationToken cancellation)
    {
        await _store.RequireTargetAsync(cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        lock (_gate)
        {
            RequireOpen();
            if (_attempted) throw new IOException("execution_android_command_already_attempted");
            _attempted = true;
            var authority = Authority();
            try
            {
                _started = authority.Use(_job, request => _job.Start(request.Command.ExecutablePath, request.Command.Arguments,
                    request.Command.WorkingDirectory, request.Binding.InstanceId, request.Binding.AttemptId,
                    root => _store.AdmitRoot(authority, _job, root), _store.IsEmulatorLaunch(request) ? null : _output));
                if (IsEmulatorLaunch) _output.CloseUnstartedEnds();
                _startedTimestamp = Stopwatch.GetTimestamp();
            }
            catch { _output.CloseUnstartedEnds(); throw; }
        }
    }

    internal async Task<PreparedAndroidObservation?> ObserveAsync(TimeSpan timeout, CancellationToken cancellation)
    {
        OwnedWorkerIdentity root;
        lock (_gate)
        {
            RequireOpen();
            if (_observation is not null) return _observation;
            root = _started ?? throw new IOException("execution_android_command_start_unconfirmed");
        }
        var deadline = Stopwatch.StartNew();
        if (Request.OwnedHandoff && _job.ReadExitCode(root) is null)
        {
            cancellation.ThrowIfCancellationRequested();
            await _capture.Completion.ConfigureAwait(false); await _lines.ConfigureAwait(false);
            lock (_gate)
            {
                RequireOpen();
                return _job.UseObservedMember(Identity(root), () =>
                {
                    var request = Request;
                    if (_store.ReadRoot(request) != Identity(root) || _job.ReadExitCode(root) is not null)
                        throw new IOException("execution_emulator_handoff_root_unbound");
                    _observation = new(this, new(AndroidControlCodec.ObservationFormat, 1, request.Binding,
                        AndroidCommandIdentity.Digest(request), Identity(root), null, true, true, [], EngineOutcome.Unknown,
                        checked((long)Stopwatch.GetElapsedTime(_startedTimestamp!.Value).TotalMilliseconds), AndroidResponseCompletion.OwnedLaunchHandoff));
                    return _observation;
                });
            }
        }
        if (await _job.AwaitRootAsync(root, timeout, cancellation).ConfigureAwait(false) is null) return null;
        lock (_gate) _elapsedMilliseconds ??= checked((long)Stopwatch.GetElapsedTime(_startedTimestamp!.Value).TotalMilliseconds);
        var remaining = timeout - deadline.Elapsed;
        if (remaining <= TimeSpan.Zero) return null;
        var drain = await _capture.DrainAsync(remaining < TimeSpan.FromMinutes(1) ? remaining : TimeSpan.FromMinutes(1)).ConfigureAwait(false);
        if (deadline.Elapsed > timeout || drain.DeadlineExceeded || !drain.StandardOutputEof || !drain.StandardErrorEof) return null;
        await _lines.ConfigureAwait(false);
        lock (_gate)
        {
            RequireOpen();
            if (_observation is not null) return _observation;
            return _job.UseExitedRoot(root, code =>
            {
                var request = Request;
                var parts = _raw.Persist(_store.Blobs);
                Hold(parts.Select(part => part.Content));
                var document = new AndroidCommandObservation(AndroidControlCodec.ObservationFormat, 1,
                    request.Binding, AndroidCommandIdentity.Digest(request), Identity(root), code, true, _raw.IsComplete,
                    parts, code == 0 ? EngineOutcome.Succeeded : EngineOutcome.Failed,
                    _elapsedMilliseconds, _raw.IsComplete ? AndroidResponseCompletion.NaturalRootExitAndEofBeforeStop :
                        AndroidResponseCompletion.Unverified);
                _observation = new(this, document); return _observation;
            });
        }
    }

    internal Task StopAsync(TimeSpan timeout)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); _closing = true;
            if (_handoff is not null || _job.HasTargetLifetimeChange) throw new IOException("execution_emulator_lifetime_transferred");
            _output.CloseUnstartedEnds();
            if (_startedTimestamp is not null) _elapsedMilliseconds ??= checked((long)Stopwatch.GetElapsedTime(_startedTimestamp.Value).TotalMilliseconds);
            if (_stopped is not null) return Task.CompletedTask;
            if (_stopping is { IsCompleted: false }) return _stopping;
            return _stopping = StopCoreAsync(timeout);
        }
    }

    private async Task StopCoreAsync(TimeSpan timeout)
    {
        var stopped = await PreparedStoppedTarget.StopAsync(_job, timeout).ConfigureAwait(false);
        lock (_gate)
        {
            _stopped = stopped;
        }
    }

    internal async Task<PreparedAndroidCollection> CollectAsync(TimeSpan stopTimeout, TimeSpan drainTimeout)
    {
        await StopAsync(stopTimeout).ConfigureAwait(false);
        var drain = await _capture.DrainAsync(drainTimeout).ConfigureAwait(false);
        if (drain.DeadlineExceeded || !drain.StandardOutputEof || !drain.StandardErrorEof)
            throw new IOException("execution_android_command_output_pending");
        await _lines.ConfigureAwait(false);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_collection is not null) return _collection;
            _authority ??= _store.ResolveRegistered(this, _job);
            var request = Request;
            if (_observation is null)
            {
                var root = _store.ReadRoot(request);
                var code = _started is null ? null : _job.ReadExitCode(_started);
                var parts = _raw.Persist(_store.Blobs); Hold(parts.Select(part => part.Content));
                var total = _stopped!.Use(_job, inventory => inventory.TotalProcesses);
                var engine = root is null ? total == 0 ? EngineOutcome.NotStarted : EngineOutcome.Unknown :
                    code is null ? EngineOutcome.Unknown : code == 0 ? EngineOutcome.Succeeded : EngineOutcome.Failed;
                var document = new AndroidCommandObservation(AndroidControlCodec.ObservationFormat, 1,
                    request.Binding, AndroidCommandIdentity.Digest(request), root, code, _started is not null, _raw.IsComplete,
                    parts, engine, _started is null ? null : _elapsedMilliseconds, AndroidResponseCompletion.Unverified);
                _observation = new(this, document);
            }
            return _stopped!.Use(_job, inventory => _collection = new(this, _observation, inventory));
        }
    }

    internal Task<PreparedAndroidCollection> FinishLaunchAsync(TimeSpan stopTimeout, TimeSpan drainTimeout)
    {
        lock (_gate)
        {
            if (_collection is not null) return Task.FromResult(_collection);
            RequireOpen();
            if (!IsEmulatorLaunch || _observation is null) throw new IOException("execution_emulator_launch_unobserved");
            if (_job.CaptureTargetInventory().ActiveProcesses == 0) return CollectAsync(stopTimeout, drainTimeout);
            _retention ??= _store.PrepareRetention(Authority(), _job);
            _retentionIntent ??= new(_retention);
            _handoff ??= _job.HandoffTarget(_retentionIntent);
            Retention ??= _store.RecordRetained(Authority(), _job, _retention, _handoff);
            _closing = true;
            _collection ??= new(this, _observation, _handoff.Inventory with { LimitFlags = 0 });
            return Task.FromResult(_collection);
        }
    }

    internal async Task ReleaseUnregisteredAsync(TimeSpan stopTimeout, TimeSpan drainTimeout)
    {
        await StopAsync(stopTimeout).ConfigureAwait(false);
        var drain = await _capture.DrainAsync(drainTimeout).ConfigureAwait(false);
        if (drain.DeadlineExceeded || !drain.StandardOutputEof || !drain.StandardErrorEof)
            throw new IOException("execution_android_command_output_pending");
        await _lines.ConfigureAwait(false);
        lock (_gate)
        {
            if (_authority is not null || _attempted || _started is not null) throw new IOException("execution_android_command_was_registered");
            _store.RequireUnregistered(this, _job);
            _stopped!.Use(_job, inventory =>
            {
                if (inventory.TotalProcesses != 0 || inventory.RetainedKernelObjects != 0)
                    throw new IOException("execution_android_unregistered_effects_unknown");
                return true;
            });
            _job.Dispose(); _output.Dispose(); _releasedEmpty = true; _disposed = true;
        }
    }

    internal T UseObserved<T>(PreparedAndroidObservation proof, Func<AndroidCommandObservation, T> callback)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!ReferenceEquals(proof, _observation)) throw new IOException("execution_android_command_observation_unbound");
            _store.RequireLive(); Authority().Use(_job, _ => true);
            if (_stopped is not null) return _stopped.Use(_job, _ => callback(proof.Document));
            RequireOpen();
            if (proof.Document.Completion == AndroidResponseCompletion.OwnedLaunchHandoff)
                return _job.UseObservedMember(proof.Document.Root!, () => callback(proof.Document));
            return _job.UseExitedRoot(_started ?? throw new IOException("execution_android_command_start_unconfirmed"), code =>
            {
                if (proof.Document.ExitCode != code) throw new IOException("execution_android_command_exit_changed");
                return callback(proof.Document);
            });
        }
    }

    internal T UseCollected<T>(PreparedAndroidCollection proof,
        Func<AndroidCommandObservation, OwnedTargetInventory, T> callback)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!ReferenceEquals(proof, _collection) || _stopped is null && _handoff is null || _observation is null)
                throw new IOException("execution_android_command_collection_unbound");
            _store.RequireLive(); Authority().Use(_job, _ => true);
            if (_handoff is not null) return _job.UseTargetHandoff(_handoff, () => callback(_observation.Document, proof.Inventory));
            return _stopped!.Use(_job, inventory => callback(_observation.Document, inventory));
        }
    }

    private void Hold(IEnumerable<BlobReference> parts)
    { foreach (var part in parts) _holds.Add(_store.Blobs.OpenVerified(part)); }

    private PreparedAndroidAuthority Authority() => _authority ?? throw new IOException("execution_android_command_unregistered");
    private void RequireOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this); _store.RequireLive();
        if (_closing || _identities.Overflow) throw new IOException("execution_android_command_admission_closed");
    }
    private static ProcessIdentity Identity(OwnedWorkerIdentity root) =>
        new(root.ProcessId, root.CreatedAtUtc, root.ImagePath, root.ImageSha256, root.UserSid, root.IsElevated);
    private async Task DiscardLinesAsync()
    { await foreach (var _ in _capture.Lines.ReadAllAsync().ConfigureAwait(false)) { } }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed || _releasedEmpty) return;
            if (_stopped is null && _handoff is null || _collection is null || !_capture.Completion.IsCompleted || !_lines.IsCompleted)
                throw new IOException("execution_android_command_collection_pending");
            _store.RequireReferenced(Request.Binding);
            _job.Dispose(); _output.Dispose();
            for (var index = _holds.Count - 1; index >= 0; index--) _holds[index].Dispose();
            _disposed = true;
        }
    }
}
