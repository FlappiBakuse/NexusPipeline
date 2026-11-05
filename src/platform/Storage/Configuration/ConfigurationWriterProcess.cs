using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Storage.Configuration;

[SupportedOSPlatform("windows")]
internal sealed class ConfigurationWriterProcess : IDisposable
{
    private readonly object _gate = new();
    private readonly RuntimeInstance _instance;
    private readonly InstanceLease _lease;
    private readonly IConfigurationWriterScope _scope;
    private readonly IWriterPersistenceObserver? _persistenceObserver;
    private readonly Guid _operationId;
    private readonly Guid _processScopeId;
    private readonly OwnedWorkerJob _job;
    private readonly ProcessStartObserver _observer;
    private readonly Dictionary<(int, DateTime), ProcessIdentity> _members = [];
    private readonly ImmutableArray<Guid> _journals;
    private readonly ConfigurationEditorCommand? _command;
    private readonly CommandLineSyntax _commandLineSyntax;
    private OwnedWorkerIdentity? _root;
    private bool _rootPublished;
    private bool _startAttempted;
    private bool _registrationCompleted;
    private bool _closed;
    private long? _startedTimestamp;
    private TimeSpan? _elapsedAtClose;
    private StoppedConfigurationEditor? _stopped;
    private FrozenJobClosure? _closedFacts;
    private Task<StoppedConfigurationEditor>? _stopping;
    private int? _observedExitCode;
    private OwnedProcessOutput? _startedOutput;
    private readonly Guid? _executionParentJournalId;

    internal ConfigurationWriterProcess(RuntimeInstance instance, InstanceLease lease, IConfigurationWriterScope scope,
        IWriterPersistenceObserver? persistenceObserver = null, bool deferRegistration = false)
    {
        lease.VerifyHeld(instance); ArgumentNullException.ThrowIfNull(scope);
        _instance = instance; _lease = lease; _scope = scope;
        _persistenceObserver = persistenceObserver;
        _operationId = scope.OperationId; _processScopeId = scope.ProcessScopeId;
        _executionParentJournalId = scope switch
        {
            StepWriterScope execution => execution.JournalIds[0],
            AttemptWriterScope composite => composite.ParentJournalId,
            _ => null
        };
        _commandLineSyntax = scope.CommandLineSyntax;
        if (!Enum.IsDefined(_commandLineSyntax)) throw new IOException("configuration_writer_command_syntax_invalid");
        var journalIds = scope.JournalIds;
        _journals = journalIds.IsDefault ? [] : journalIds.ToImmutableArray();
        _command = scope.Command is { } command ? EditorCommandPolicy.Freeze(command) : null;
        if (_operationId == Guid.Empty || _processScopeId == Guid.Empty || _journals.Length is < 1 or > 32 ||
            _journals.Any(id => id == Guid.Empty) || _journals.Distinct().Count() != _journals.Length ||
            !_journals.SequenceEqual(_journals.OrderBy(id => id.ToString("D"), StringComparer.Ordinal)))
            throw new IOException("configuration_writer_scope_invalid");
        scope.RequirePrepared();
        _job = new();
        ProcessStartObserver? observer = null;
        try
        {
            _observer = observer = _job.ObserveProcessIdentities(process =>
            {
                var pid = process.ProcessId;
                lock (_members)
                {
                    if (_members.Count >= 4096 && !_members.ContainsKey((pid, process.CreatedAtUtc))) throw new IOException("configuration_editor_family_limit");
                    _members[(pid, process.CreatedAtUtc)] = process;
                }
            });
            if (!deferRegistration) RegisterPreparedJob();
        }
        catch { observer?.Dispose(); _job.Dispose(); throw; }
    }

    internal void RegisterPreparedJob()
    {
        lock (_gate)
        {
            _lease.VerifyHeld(_instance);
            if (_closed || _startAttempted) throw new IOException("configuration_writer_registration_closed");
            if (_registrationCompleted) return;
            _scope.RegisterJob(_job); _registrationCompleted = true;
        }
    }

    internal void RequireStoppedUnregistered(StoppedConfigurationEditor stopped)
    {
        lock (_gate)
        {
            _lease.VerifyHeld(_instance);
            if (_registrationCompleted || !ReferenceEquals(stopped, _stopped) || !_closed || _startedTimestamp is not null ||
                stopped.WasExecuted || stopped.Closure.RunElapsedMilliseconds != 0 || _job.GetAccounting().ActiveProcesses != 0 ||
                _job.GetAccounting().TotalProcesses != 0 || stopped.Closure.Family.Length != 0)
                throw new IOException("configuration_writer_unregistered_stop_unverified");
        }
    }

    internal void RequireRegisteredJob()
    {
        lock (_gate)
        {
            _lease.VerifyHeld(_instance);
            if (!_registrationCompleted) throw new IOException("configuration_writer_registration_incomplete");
        }
    }

    internal string JobName => _job.Name;

    internal void ReleaseEmptyJob()
    {
        lock (_gate)
        {
            _lease.VerifyHeld(_instance);
            var accounting = _job.GetAccounting();
            if (_closed || _registrationCompleted || _startAttempted || _root is not null || _rootPublished || _startedTimestamp is not null ||
                _stopped is not null || _closedFacts is not null || _stopping is not null || accounting.TotalProcesses != 0 ||
                accounting.ActiveProcesses != 0 || _job.ActiveProcessIds().Count != 0)
                throw new IOException("execution_unregistered_job_release_unverified");
            var scope = _scope switch
            {
                StepWriterScope execution => execution,
                AttemptWriterScope composite => composite.RequireUnregisteredParent(),
                _ => throw new IOException("execution_unregistered_job_release_unverified")
            };
            lock (_members)
                if (_members.Count != 0) throw new IOException("execution_unregistered_job_release_unverified");
            _lease.PublishExecution(() => scope.RequireUnregisteredJob(_job.Name));
            _closed = true; _observer.Dispose(); _job.Dispose();
        }
    }

    internal void RequireInterpreterChild(ProcessIdentity child)
    {
        lock (_gate)
        {
            _lease.VerifyHeld(_instance);
            if (_closed || !_registrationCompleted || _startedTimestamp is null || !_observer.Drain(TimeSpan.FromSeconds(5)))
                throw new IOException("judgement_python_child_admission_closed");
            using var process = Process.GetProcessById(child.ProcessId);
            if (!_job.ContainsProcessHandle(process.SafeHandle) || process.StartTime.ToUniversalTime() != child.CreatedAtUtc)
                throw new IOException("judgement_python_child_not_owned");
            lock (_members)
                if (!_members.TryGetValue((child.ProcessId, child.CreatedAtUtc), out var observed) || observed != child)
                    throw new IOException("judgement_python_child_not_observed");
        }
    }

    internal T UseJudgementEvidence<T>(StepEvidenceBinding primary, StoppedConfigurationEditor? stopped,
        Func<ProcessIdentity?, T> callback)
    {
        lock (_gate)
        {
            _lease.VerifyHeld(_instance);
            if (_scope is not StepWriterScope { Role: "judgement" } scope || scope.StepId != primary.StepId ||
                primary.InstanceId != _instance.InstanceId || primary.AttemptId != _processScopeId ||
                primary.ParentJournalId != _executionParentJournalId || primary.JobName == _job.Name ||
                stopped is not null && (!ReferenceEquals(stopped, _stopped) || _job.GetAccounting().ActiveProcesses != 0))
                throw new IOException("judgement_native_owner_unbound");
            return callback(_rootPublished && _root is not null ? new(_root.ProcessId, _root.CreatedAtUtc,
                _root.ImagePath, _root.ImageSha256, _root.UserSid, _root.IsElevated) : null);
        }
    }

    internal Task AdmitJudgementWrite(Func<CancellationToken, Task> write, CancellationToken cancellation,
        OwnedProcessOutput output, TimeSpan timeout) =>
        AdmitControlWrite(write, cancellation, output, timeout, () =>
        {
            if (_scope is not StepWriterScope { Role: "judgement" } scope)
                throw new IOException("judgement_invocation_admission_closed");
            scope.RequireJudgementInvocation();
        }, "judgement_invocation");

    internal Task AdmitProviderBootstrap(StepEvidenceBinding binding, OwnedProcessOutput output,
        Func<CancellationToken, Task> write, TimeSpan timeout) =>
        AdmitControlWrite(write, default, output, timeout, () =>
        {
            var parent = _scope switch
            {
                StepWriterScope scope => scope,
                AttemptWriterScope composite => composite.RequireProviderParent(),
                _ => throw new IOException("execution_provider_admission_closed")
            };
            parent.RequireProviderBootstrap(binding);
        }, "execution_provider");

    private Task AdmitControlWrite(Func<CancellationToken, Task> write, CancellationToken cancellation,
        OwnedProcessOutput output, TimeSpan timeout, Action requireAdmission, string code)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        lock (_gate)
        {
            _lease.VerifyHeld(_instance);
            var started = _startedTimestamp ?? throw new IOException(code + "_admission_closed");
            if (_closed || !_registrationCompleted || !ReferenceEquals(output, _startedOutput))
                throw new IOException("judgement_invocation_admission_closed");
            _lease.PublishExecution(() =>
            {
                requireAdmission();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                deadline.CancelAfter(timeout);
                Task? pending = null;
                try
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    pending = write(deadline.Token);
                    pending.WaitAsync(deadline.Token).GetAwaiter().GetResult();
                }
                catch
                {
                    // No pending pipe write may outlive the admission that ordered it before cancellation.
                    _closed = true;
                    _elapsedAtClose ??= Stopwatch.GetElapsedTime(started);
                    try
                    {
                        if (!_job.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult())
                            throw new IOException(code + "_stop_unconfirmed");
                    }
                    finally
                    {
                        output.CompleteInput();
                        if (pending is not null)
                            try { pending.GetAwaiter().GetResult(); } catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
                    }
                    throw;
                }
            });
            return Task.CompletedTask;
        }
    }

    internal T UseTargetReadiness<T>(PcTargetReadiness readiness, Func<ImmutableArray<ProcessIdentity>, T> callback)
    {
        lock (_gate)
        {
            _lease.VerifyHeld(_instance);
            if (_closed || !_registrationCompleted || _startedTimestamp is null || !_rootPublished ||
                _scope is not StepWriterScope { Role: not "judgement" })
                throw new IOException("execution_target_native_owner_unbound");
            return readiness.UseOwned(_job, callback);
        }
    }

    internal TimeSpan ElapsedSinceStart
    {
        get
        {
            lock (_gate) return _startedTimestamp is { } started ? _elapsedAtClose ?? Stopwatch.GetElapsedTime(started)
                : _stopped is not null ? TimeSpan.Zero : throw new IOException("configuration_writer_not_started");
        }
    }

    internal void RequireScope(RuntimeInstance instance, Guid operationId)
    {
        if (_instance != instance || _operationId != operationId) throw new IOException("configuration_writer_scope_unbound");
        _lease.VerifyHeld(instance);
    }

    internal OwnedWorkerIdentity Start(OwnedProcessOutput? output = null)
    {
        var command = _command ?? throw new IOException("configuration_editor_command_required");
        return Start(command.ExecutablePath, command.ExecutableSha256, command.Arguments, command.WorkingDirectory, output);
    }

    internal OwnedWorkerIdentity Start(string executable, string expectedSha256, IReadOnlyList<string> arguments, string workingDirectory,
        OwnedProcessOutput? output = null)
    {
        lock (_gate)
        {
            _lease.VerifyHeld(_instance);
            if (!_registrationCompleted) throw new IOException("configuration_writer_registration_incomplete");
            if (_closed || _startAttempted) throw new IOException("configuration_editor_start_closed");
            var frozenArguments = arguments.ToImmutableArray();
            if (_command is not null && !EditorCommandPolicy.Equal(_command, new(executable, expectedSha256, frozenArguments, workingDirectory) { ExclusiveProgram = _command.ExclusiveProgram }))
                throw new IOException("configuration_editor_command_changed");
            _startAttempted = true;
            _startedOutput = output;
            _scope.RequireStart();
            if (_command?.ExclusiveProgram == true) ExclusiveProgramAdmission.RequireStopped(executable);
            var started = StartOwned(executable, expectedSha256, frozenArguments, workingDirectory, output);
            _startedTimestamp = Stopwatch.GetTimestamp();
            return started;
        }
    }

    private OwnedWorkerIdentity StartOwned(string executable, string expectedSha256, IReadOnlyList<string> arguments, string workingDirectory,
        OwnedProcessOutput? output)
    {
        if (!FileTreePolicy.Digest(expectedSha256)) throw new IOException("configuration_editor_image_invalid");
        using var image = new FileStream(PhysicalPathPolicy.NormalizeWithoutLinks(executable), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexStringLower(SHA256.HashData(image)) != expectedSha256) throw new IOException("configuration_editor_image_changed");
        return _job.StartFrozen(executable, arguments, workingDirectory, _instance.InstanceId, _processScopeId, identity =>
        {
            _root = identity;
            var process = new ProcessIdentity(identity.ProcessId, identity.CreatedAtUtc, identity.ImagePath, identity.ImageSha256, identity.UserSid, identity.IsElevated);
            lock (_members) _members[(process.ProcessId, process.CreatedAtUtc)] = process;
            _scope.RecordRoot(_job.Name, process);
            _lease.VerifyHeld(_instance);
            _rootPublished = true;
            _persistenceObserver?.Reached(WriterPersistencePoint.RootPublished, process);
        }, output, _commandLineSyntax);
    }

    internal Task<int?> AwaitRootAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _lease.VerifyHeld(_instance);
            if (_root is null || !_rootPublished || _startedTimestamp is null) throw new IOException("configuration_writer_start_outcome_unknown");
            return _job.AwaitRootAsync(_root, timeout, cancellationToken);
        }
    }

    internal async Task<int?> WaitForWorkAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var elapsed = Stopwatch.StartNew();
        var code = await AwaitRootAsync(timeout, cancellationToken).ConfigureAwait(false);
        if (code is null) return null;
        while (_job.GetAccounting().ActiveProcesses != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (elapsed.Elapsed >= timeout) return null;
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
        }
        return code;
    }

    internal T UsePrimaryParent<T>(StepEvidenceBinding binding, Func<OwnedWorkerJob, OwnedWorkerIdentity, T> start)
    {
        lock (_gate)
        {
            _lease.VerifyHeld(_instance);
            if (_closed || !_registrationCompleted || !_rootPublished || _root is null || _startedTimestamp is null ||
                binding.InstanceId != _instance.InstanceId || binding.AttemptId != _processScopeId ||
                binding.ParentJournalId != _executionParentJournalId || binding.JobName != _job.Name)
                throw new IOException("execution_capture_writer_closed");
            var scope = _scope switch
            {
                StepWriterScope { Role: "process" } actual => actual,
                AttemptWriterScope composite => composite.RequireCaptureParent(),
                _ => throw new IOException("execution_capture_writer_unbound")
            };
            scope.RequireCaptureParent(binding, _root);
            return start(_job, _root);
        }
    }

    internal T UseStepEvidence<T>(StepEvidenceBinding binding, StoppedConfigurationEditor? stopped,
        Func<ProcessIdentity?, int?, T> callback)
    {
        lock (_gate)
        {
            _lease.VerifyHeld(_instance);
            if (binding.InstanceId != _instance.InstanceId || binding.JobName != _job.Name || binding.AttemptId != _processScopeId ||
                _executionParentJournalId != binding.ParentJournalId || (stopped is not null && !ReferenceEquals(stopped, _stopped)))
                throw new IOException("execution_step_native_owner_unbound");
            if (stopped is not null && (!_closed || _job.GetAccounting().ActiveProcesses != 0 ||
                stopped.Closure.Family.Any(member => WriterStopEvidence.ObserveProcess(member) != WriterState.Stopped)))
                throw new IOException("execution_step_native_collection_unverified");
            if (!_closed && _rootPublished && _root is not null && _startedTimestamp is not null) _observedExitCode ??= _job.ReadExitCode(_root);
            var root = _rootPublished && _root is not null ? new ProcessIdentity(_root.ProcessId, _root.CreatedAtUtc,
                _root.ImagePath, _root.ImageSha256, _root.UserSid, _root.IsElevated) : null;
            return callback(root, _observedExitCode);
        }
    }

    internal Task<StoppedConfigurationEditor> StopAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        lock (_gate)
        {
            _lease.VerifyHeld(_instance);
            if (_stopped is not null) return Task.FromResult(_stopped);
            if (_closedFacts is not null && _stopping is { IsCompleted: true })
                return _stopping = PersistStoppedAsync();
            if (_stopping is not null) return _stopping;
            if (!_closed)
            {
                if (_rootPublished && _root is not null && _startedTimestamp is not null) _observedExitCode ??= _job.ReadExitCode(_root);
                _closed = true;
                _elapsedAtClose = _startedTimestamp is { } started ? Stopwatch.GetElapsedTime(started) : TimeSpan.Zero;
            }
            _scope.CloseAdmission(_elapsedAtClose!.Value.Ticks / TimeSpan.TicksPerMillisecond);
            return _stopping = StopOwnedAsync(timeout);
        }
    }

    private async Task<StoppedConfigurationEditor> StopOwnedAsync(TimeSpan timeout)
    {
        if (!_observer.Drain(TimeSpan.FromSeconds(5))) throw new IOException("configuration_editor_observer_before_stop_pending");
        if (!await _job.StopAsync(timeout).ConfigureAwait(false))
            throw new IOException("configuration_editor_stop_unconfirmed", new IOException(System.Text.Json.JsonSerializer.Serialize(_job.LastStopObservation)));
        if (!_observer.Drain(TimeSpan.FromSeconds(5))) throw new IOException("configuration_editor_observer_after_stop_pending");
        _lease.VerifyHeld(_instance);
        var accounting = _job.GetAccounting(); ProcessIdentity[] members;
        lock (_members) members = _members.Values.OrderBy(member => member.ProcessId).ThenBy(member => member.CreatedAtUtc).ToArray();
        if (_observer.Failures.Length != 0 || accounting.ActiveProcesses != 0 || _job.ActiveProcessIds().Count != 0 || accounting.TotalProcesses != members.Length ||
            members.Any(member => WriterStopEvidence.ObserveProcess(member) != WriterState.Stopped)) throw new IOException("configuration_editor_family_unverified");
        if (_rootPublished && _startedTimestamp is null) throw new IOException("configuration_writer_start_outcome_unknown");
        var root = _root is null ? members.SingleOrDefault() : new ProcessIdentity(_root.ProcessId, _root.CreatedAtUtc, _root.ImagePath, _root.ImageSha256, _root.UserSid, _root.IsElevated);
        var writer = JournalWriterStore.CurrentWriter;
        using var current = Process.GetCurrentProcess();
        var closure = new FrozenJobClosure(_instance.InstanceId, _operationId, writer.WriterId, writer.Process, _job.Name,
            current.SessionId, _journals, root, members.ToImmutableArray(), _rootPublished, DateTimeOffset.UtcNow);
        _closedFacts = _lease.PublishExecution(() => _scope.BindClosure(closure, _elapsedAtClose!.Value.Ticks / TimeSpan.TicksPerMillisecond));
        return await PersistStoppedAsync().ConfigureAwait(false);
    }

    private Task<StoppedConfigurationEditor> PersistStoppedAsync()
    {
        try
        {
            _lease.VerifyHeld(_instance);
            var closure = _closedFacts!;
            foreach (var member in closure.Family)
                if (WriterStopEvidence.ObserveProcess(member) != WriterState.Stopped) throw new IOException("configuration_editor_family_unverified");
            var reference = _lease.PublishExecution(() => _scope.RecordClosed(closure));
            return Task.FromResult(_stopped = new(closure, reference));
        }
        catch (Exception error) { return Task.FromException<StoppedConfigurationEditor>(error); }
    }

    public void Dispose() { _observer.Dispose(); _job.Dispose(); }
}
