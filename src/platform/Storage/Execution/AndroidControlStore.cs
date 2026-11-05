using System.Runtime.Versioning;
using System.Collections.Immutable;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed partial class AndroidControlStore : IDisposable
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly InstanceLease _lease;
    private readonly ResourceOperationBorrow _root;
    private readonly PreparedExecutionAdmission _prepared;
    private readonly ITargetPersistenceObserver? _observer;
    private readonly Guid _journalId, _operationId;
    private readonly string _stepId;
    private readonly AndroidControlClock _clock;
    private ImmutableDictionary<string, (AndroidControlProcess Node, OwnedWorkerJob Job, PreparedAndroidAuthority Authority)> _issued =
        ImmutableDictionary<string, (AndroidControlProcess, OwnedWorkerJob, PreparedAndroidAuthority)>.Empty;
    private ImmutableDictionary<Guid, BlobReference> _observations = ImmutableDictionary<Guid, BlobReference>.Empty;
    private ImmutableDictionary<Guid, BlobReference> _collections = ImmutableDictionary<Guid, BlobReference>.Empty;
    private BlobReference? _published, _startup;
    private PreparedCaptureContext? _capture;
    private PreparedAndroidDevice? _device;
    private readonly List<IDisposable> _held = [];
    internal ImmutableBlobStore Blobs { get; }

    private AndroidControlStore(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, ResourceOperationBorrow root, PreparedExecutionAdmission prepared, Guid journalId, Guid operationId, string stepId, AndroidControlClock clock, ITargetPersistenceObserver? observer)
    {
        _instance = instance; _database = database; _contracts = contracts; _lease = lease; _root = root; _prepared = prepared;
        _journalId = journalId; _operationId = operationId; _stepId = stepId; _clock = clock; _observer = observer; Blobs = new(instance, contracts);
    }

    internal static AndroidControlStore ForAccepted(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, ResourceLeaseManager resources, AcceptedExecutionAdmission accepted, string stepId, ITargetPersistenceObserver? observer = null)
    {
        lease.VerifyHeld(instance); accepted.Prepared.RequireLive();
        var total = accepted.Prepared.Total.Plan.ReadWire();
        if (accepted.Root.OperationId != accepted.Identity.OperationId ||
            total.InstanceId != instance.InstanceId.ToString("D") ||
            total.Steps.Single(item => item.StepId == stepId).Target?.Kind != "android")
            throw new IOException("execution_android_attempt_unbound");
        var borrow = accepted.Root.Borrow(resources, accepted.Identity.OperationId, ExecutionPlanResources.Claims(total));
        return new(instance, database, contracts, lease, borrow, accepted.Prepared, accepted.Receipt.JournalId, accepted.Identity.OperationId, stepId,
            new(accepted.Prepared.TimeBudget, TimeSpan.FromSeconds(Math.Min(total.Budget.StopTimeoutSeconds, 60))), observer);
    }

    internal PreparedAndroidAuthority Register(AndroidControlProcess node, OwnedWorkerJob job, int sequence,
        AndroidCommandPurpose purpose, ExecutionAndroidAction action, BlobReference? previousObservation) =>
        _root.Use(() => Publish(() =>
    {
        var state = ReadSynchronized();
        _clock.Require(AndroidControlAdmission.IsCleanup(purpose));
        if (_issued.ContainsKey(job.Name)) throw new IOException("execution_android_command_already_registered");
        if (job.GetAccounting() is not { ActiveProcesses: 0, TotalProcesses: 0 } || job.ActiveProcessIds().Count != 0)
            throw new IOException("execution_android_command_not_empty");
        var registrations = Registrations(state);
        if (sequence != registrations.Count || registrations.Any(item => item.Binding.JobName == job.Name))
            throw new IOException("execution_android_command_order_changed");
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var binding = new ExecutionTargetBinding(_instance.InstanceId, _journalId, _operationId, Guid.Parse(state.Attempt.AttemptId),
            _stepId, Guid.NewGuid(), job.Name, JournalWriterStore.CurrentWriter.WriterId, process.SessionId, state.Attempt.ExecutionPlan.Sha256);
        var reader = Reader();
        var request = AndroidCommandIdentity.Derive(state.Total, binding, reader.Subject(state), sequence, purpose, action);
        if (registrations.Count > 0 && (!_observations.TryGetValue(registrations[^1].Binding.TargetId, out var actualPrevious) ||
                actualPrevious != previousObservation)) throw new IOException("execution_android_previous_observation_missing");
        if (registrations.Count == 0 && previousObservation is not null) throw new IOException("execution_android_previous_observation_unexpected");
        AndroidControlSequence.RequireNext(_instance, _database, _contracts, state, request, registrations, previousObservation);
        _database.Write((connection, transaction) =>
        {
            AndroidControlAdmission.RequireInside(connection, transaction, state, request); return true;
        });
        var requestRef = Persist(request);
        var plan = registrations.Count == 0 ? Persist(new AndroidControlPlan(AndroidControlCodec.ControlPlanFormat, 1,
            binding.InstanceId, binding.ParentJournalId, binding.ParentOperationId, binding.AttemptId,
            binding.StepId, binding.TotalPlanSha256, request.Limits)) : registrations[0].ControlPlan;
        var registration = new AndroidCommandRegistration(AndroidControlCodec.RegistrationFormat, 1, binding,
            sequence, purpose, action, requestRef, AndroidCommandIdentity.Digest(request), plan, previousObservation);
        TargetJournalPublisher.AppendParticipant(_instance, _database, _contracts, state,
            AndroidControlCodec.Participant(registration, state.Total.Steps.Single(step => step.StepId == _stepId).ScriptId));
        var authority = new PreparedAndroidAuthority(this, job, request);
        ImmutableInterlocked.TryAdd(ref _issued, job.Name, (node, job, authority)); return authority;
    }));

    internal PreparedAndroidAuthority? ResolveRegistered(AndroidControlProcess node, OwnedWorkerJob job) =>
        _root.Use(() => Publish(() =>
    {
        if (_issued.TryGetValue(job.Name, out var issued))
        {
            if (!ReferenceEquals(issued.Node, node) || !ReferenceEquals(issued.Job, job)) throw Invalid();
            return issued.Authority;
        }
        var state = ReadSynchronized();
        var registration = Registrations(state).SingleOrDefault(item => item.Binding.JobName == job.Name);
        if (registration is null) return null;
        if (registration.Binding.OwnerWriterId != JournalWriterStore.CurrentWriter.WriterId) throw Invalid();
        var authority = new PreparedAndroidAuthority(this, job, Reader().Request(state, registration));
        ImmutableInterlocked.TryAdd(ref _issued, job.Name, (node, job, authority)); return authority;
    }));

    internal void AdmitRoot(PreparedAndroidAuthority authority, OwnedWorkerJob actualJob, OwnedWorkerIdentity root) =>
        authority.Use(actualJob, request => Publish(() =>
    {
        var state = ReadSynchronized();
        var registration = Required(state, request.Binding);
        _clock.Require(AndroidControlAdmission.IsCleanup(request.Purpose));
        AndroidControlSequence.RequireNext(_instance, _database, _contracts, state, request,
            Registrations(state).Take(request.Sequence).ToArray(), registration.PreviousObservation);
        if (root.InstanceId != _instance.InstanceId || root.AttemptId != request.Binding.AttemptId ||
            root.ImageSha256 != request.Tool.Sha256 || !StringComparer.OrdinalIgnoreCase.Equals(root.ImagePath, request.Tool.Path))
            throw new IOException("execution_android_native_root_unbound");
        using var process = System.Diagnostics.Process.GetProcessById(root.ProcessId);
        var identity = new ProcessIdentity(root.ProcessId, root.CreatedAtUtc, root.ImagePath, root.ImageSha256, root.UserSid, root.IsElevated);
        if (!actualJob.ContainsProcessHandle(process.SafeHandle) || ProcessIdentityReader.Read(root.ProcessId) != identity ||
            actualJob.GetAccounting() is not { ActiveProcesses: 1, TotalProcesses: 1 } ||
            !actualJob.ActiveProcessIds().SequenceEqual([root.ProcessId])) throw Invalid();
        _database.Write((connection, transaction) =>
        {
            AndroidControlRows.RequireRegisteredInside(connection, transaction, registration);
            _clock.Require(AndroidControlAdmission.IsCleanup(request.Purpose));
            TargetRootFacts.InsertCommandInside(connection, transaction, state, registration, request, identity);
            return true;
        });
        _clock.Require(AndroidControlAdmission.IsCleanup(request.Purpose));
        _observer?.Reached(TargetPersistencePoint.RootAdmitted, request.Binding);
        return true;
    }));

    internal TimeSpan Remaining(bool cleanup) => _root.Use(() => _lease.PublishExecution(() => _clock.Remaining(cleanup)));

    internal BlobReference RecordObserved(PreparedAndroidObservation proof) => proof.Use(document =>
        _root.Use(() => Publish(() =>
    {
        var state = ReadSynchronized();
        var registration = Required(state, document.Binding);
        var reference = Persist(document);
        _ = Reader().Observation(state, registration, reference, verifyContent: true);
        if (_observations.TryGetValue(document.Binding.TargetId, out var prior) && prior != reference)
            throw new IOException("execution_android_observation_changed");
        _observations = _observations.SetItem(document.Binding.TargetId, reference); return reference;
    })));

    internal BlobReference RecordCollected(PreparedAndroidCollection proof) => proof.Use((observation, inventory) =>
        _root.Use(() => Publish(() =>
    {
        var state = ReadSynchronized();
        var registration = Required(state, observation.Binding);
        var observationRef = Persist(observation);
        _ = Reader().Observation(state, registration, observationRef, verifyContent: true);
        if (_observations.TryGetValue(observation.Binding.TargetId, out var prior) && prior != observationRef)
            throw new IOException("execution_android_observation_changed");
        var collection = new AndroidCommandCollection(AndroidControlCodec.CollectionFormat, 1,
            observation.Binding, observation.RequestDigest, observationRef, inventory) { Retention = proof.Retention };
        var reference = Persist(collection);
        _ = Reader().Collection(state, registration, reference, verifyContent: true);
        if (_collections.TryGetValue(observation.Binding.TargetId, out var previous) && previous != reference)
            throw new IOException("execution_android_collection_changed");
        _observations = _observations.SetItem(observation.Binding.TargetId, observationRef);
        _collections = _collections.SetItem(observation.Binding.TargetId, reference);
        return reference;
    })));

    internal ProcessIdentity? ReadRoot(AndroidCommandRequest request) => _root.Use(() => Publish(() =>
    {
        var registration = Required(ReadSynchronized(), request.Binding);
        return TargetRootFacts.ReadCommand(_database, registration);
    }));

    internal void RequireIssued(PreparedAndroidAuthority authority, OwnedWorkerJob job, AndroidCommandRequest request)
    {
        RequireLive();
        if (!_issued.TryGetValue(job.Name, out var issued) || !ReferenceEquals(issued.Job, job) ||
            !ReferenceEquals(issued.Authority, authority) || issued.Authority.Request.Binding != request.Binding)
            throw Invalid();
        var step = _prepared.Total.Plan.ReadWire().Steps.Single(step => step.StepId == request.Binding.StepId);
        if (step.Target?.Android?.Emulator is { } emulator)
            (_prepared.Emulators ?? throw new IOException("emulator_runtime_owner_unavailable")).RequireActivation(emulator, Guid.Parse(step.ScriptId));
    }

    internal void RequireLive() { _lease.VerifyHeld(_instance); _root.VerifyHeld(); _prepared.RequireLive(); }

    internal ValueTask RequireTargetAsync(CancellationToken cancellation)
    {
        RequireLive(); var step = _prepared.Total.Plan.ReadWire().Steps.Single(value => value.StepId == _stepId);
        return step.Target?.Android?.Emulator is null ? ValueTask.CompletedTask :
            (_prepared.Emulators ?? throw new IOException("emulator_runtime_owner_unavailable")).RequireRuntimeAsync(step.Target.Android, Guid.Parse(step.ScriptId), cancellation);
    }

    internal void RequireUnregistered(AndroidControlProcess node, OwnedWorkerJob job) =>
        _root.Use(() => Publish(() =>
    {
        var state = ReadSynchronized();
        if (_issued.ContainsKey(job.Name) || Registrations(state).Any(item => item.Binding.JobName == job.Name) ||
            state.Jobs.Any(item => item.JobName == job.Name) ||
            TargetJobStore.ReadJobs(state).Any(item => item.Binding.JobName == job.Name)) throw Invalid();
        TargetRootFacts.RequireRootAbsent(_database, _journalId, job.Name);
        return true;
    }));

    internal void RequireReferenced(ExecutionTargetBinding binding)
    {
        RequireLive();
        if (_published is null || !_collections.ContainsKey(binding.TargetId)) throw new IOException("execution_android_collection_pending");
    }

    internal BlobReference PublishStartup(TargetExecutionOutcome outcome, string reasonCode) => _root.Use(() => Publish(() =>
    {
        var state = ReadSynchronized();
        var registrations = Registrations(state);
        if (registrations.Any(item => AndroidControlAdmission.IsCleanup(item.Purpose)) ||
            registrations.Any(item => !_observations.ContainsKey(item.Binding.TargetId)))
            throw new IOException("execution_android_startup_observation_pending");
        var binding = ControlBinding(state);
        var commands = registrations.Select(item => new AndroidStartupCommand(item,
            _observations[item.Binding.TargetId])).ToImmutableArray();
        var document = new AndroidStartupDocument(AndroidStartupCodec.Format, 1, binding, outcome, reasonCode, commands);
        var artifact = Persist(document);
        var evidence = new AndroidResultEvidence(_instance, _database, _contracts);
        var intent = new AndroidStartupIntent(AndroidStartupCodec.Format, 1, binding, outcome, reasonCode,
            evidence.CommandFacts(state, commands), artifact);
        _ = evidence.RequireStartup(state, intent, verifyContent: true);
        var prior = AndroidStartupCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
            .SingleOrDefault(item => item.Binding.StepId == _stepId);
        if (prior is not null)
        {
            if (prior.Artifact != artifact) throw new IOException("execution_android_startup_changed");
            _startup = prior.Artifact; return prior.Artifact;
        }
        TargetJournalPublisher.AppendParticipant(_instance, _database, _contracts, state,
            AndroidStartupCodec.Participant(intent, state.Total.Steps.Single(step => step.StepId == _stepId).ScriptId));
        _startup = artifact; return artifact;
    }));

    internal AndroidCommandRegistration Registration(AndroidCommandRequest request) =>
        _root.Use(() => Publish(() => Required(ReadSynchronized(), request.Binding)));

    internal bool HasCleanupMapping() => _root.Use(() => Publish(() =>
    {
        var state = ReadSynchronized(); var registrations = Registrations(state);
        var previous = registrations.Count == 0 ? null : _observations.GetValueOrDefault(registrations[^1].Binding.TargetId);
        return AndroidControlSequence.CleanupMappingProven(_instance, _database, _contracts, state, registrations, previous);
    }));

    internal bool HasUnresolvedEffects() => _root.Use(() => Publish(() =>
    {
        var state = ReadSynchronized();
        var commands = Registrations(state).Where(item => _observations.ContainsKey(item.Binding.TargetId))
            .Select(item => new AndroidStartupCommand(item, _observations[item.Binding.TargetId])).ToArray();
        return new AndroidResultEvidence(_instance, _database, _contracts).CommandFacts(state, commands)
            .Any(fact => fact.Kind == TargetCommandKind.Effect && fact.Engine != EngineOutcome.NotStarted &&
                (fact.Engine == EngineOutcome.Unknown && fact.Observation != TargetCommandObservation.OwnedHandoff || fact.Observation is TargetCommandObservation.Unverified or
                    TargetCommandObservation.Incomplete or TargetCommandObservation.Contradictory));
    }));

    internal ExecutionTargetCleanup CleanupDecision() => _root.Use(() => Publish(() =>
        TargetCleanupFacts.Read(ReadSynchronized(), _stepId)));

    internal ExecutionStopReason? AcceptedStop() => _root.Use(() => Publish<ExecutionStopReason?>(() =>
    {
        var close = ReadSynchronized().Cancellation;
        return close?.ReasonCode switch
        {
            null or DispatchIntentCodec.CompletionReason => null,
            DispatchIntentCodec.Reason => ExecutionStopReason.Cancelled,
            DispatchIntentCodec.BudgetReason => ExecutionStopReason.TotalBudgetExhausted,
            DispatchIntentCodec.InterruptionReason => ExecutionStopReason.Interrupted,
            DispatchIntentCodec.TargetReason => ExecutionStopReason.TargetUnavailable,
            _ => throw new IOException("execution_android_close_unrecognized")
        };
    }));

    internal ExecutionAndroidObservation Meaning(AndroidCommandRequest request, BlobReference observation) =>
        _root.Use(() => Publish(() =>
    {
        var reader = Reader(); var state = ReadSynchronized(); var registration = Required(state, request.Binding);
        var native = reader.Observation(state, registration, observation, verifyContent: true);
        return ExecutionAndroidObservations.ReadSaved(reader, request, native, observation);
    }));

    internal BlobReference PublishCollected(TargetCleanupFact cleanup) => _root.Use(() => Publish(() =>
    {
        var state = ReadSynchronized();
        var registrations = Registrations(state);
        if (registrations.Count != _collections.Count) throw new IOException("execution_android_collection_pending");
        var binding = ControlBinding(state);
        var document = new AndroidCollectedDocument(AndroidCollectedCodec.Format, 1, binding,
            registrations.Select(item => new AndroidCollectedCommand(item.Binding,
                _collections[item.Binding.TargetId])).ToImmutableArray(),
            _startup ?? throw new IOException("execution_android_startup_pending"), cleanup);
        var artifact = Persist(document);
        var reader = Reader();
        var semantic = new AndroidResultEvidence(_instance, _database, _contracts);
        var facts = semantic.CommandFacts(state, registrations.Select(item => new AndroidStartupCommand(item,
            _observations[item.Binding.TargetId])).ToArray());
        var intent = new AndroidCollectedIntent(AndroidCollectedCodec.Format, 1, binding, artifact, facts, cleanup);
        _ = semantic.RequireCollected(state, intent, verifyContent: true);
        foreach (var registration in registrations)
        {
            var collection = reader.Collection(state, registration, _collections[registration.Binding.TargetId], verifyContent: false);
            if (collection.Retention is null && collection.Inventory.AllFamily.Any(member => WriterStopEvidence.ObserveProcess(member) != NexusPipeline.Core.Execution.WriterState.Stopped))
                throw new IOException("execution_android_full_stop_unconfirmed");
        }
        var previous = AndroidCollectedCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
            .SingleOrDefault(item => item.Binding.StepId == _stepId);
        if (previous is not null)
        {
            if (previous.Artifact != artifact) throw new IOException("execution_android_collection_changed");
            _published = previous.Artifact; return previous.Artifact;
        }
        TargetJournalPublisher.AppendParticipant(_instance, _database, _contracts, state,
            AndroidCollectedCodec.Participant(intent, state.Total.Steps.Single(step => step.StepId == _stepId).ScriptId));
        _published = artifact;
        if (registrations.Count > 0) _observer?.Reached(TargetPersistencePoint.AndroidCollectedPublished, registrations[0].Binding);
        return artifact;
    }));

    internal PreparedAndroidDevice PrepareDevice(StepEvidenceBinding primary) => _root.Use(() => Publish(() =>
    {
        var state = ReadSynchronized();
        var target = state.Total.Steps.Single(step => step.StepId == _stepId).Target?.Android ?? throw Invalid();
        var startup = AndroidStartupCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
            .SingleOrDefault(value => value.Binding.StepId == _stepId) ?? throw Invalid();
        if (_published is not null || _startup != startup.Artifact || primary.StepId != _stepId ||
            primary.ParentJournalId != _journalId || primary.ParentOperationId != _operationId || primary.InstanceId != _instance.InstanceId ||
            primary.AttemptId.ToString("D") != state.Attempt.AttemptId || primary.RunId.ToString("D") != state.Attempt.RunId ||
            startup.Outcome is not (TargetExecutionOutcome.ControlledReady or TargetExecutionOutcome.Skipped)) throw Invalid();
        if (_device is not null) return _device.Primary == primary ? _device : throw Invalid();
        _ = new AndroidResultEvidence(_instance, _database, _contracts).RequireStartup(state, startup, verifyContent: true);
        return _device = new(this, primary, target);
    }));

    internal T UseDevice<T>(PreparedAndroidDevice source, Func<T> action) => _root.Use(() =>
    {
        RequireLive();
        if (!ReferenceEquals(_device, source) || _published is not null) throw Invalid();
        var step = _prepared.Total.Plan.ReadWire().Steps.Single(value => value.StepId == _stepId);
        if (step.Target?.Android?.Emulator is { } emulator)
            (_prepared.Emulators ?? throw new IOException("emulator_runtime_owner_unavailable")).RequireActivation(emulator, Guid.Parse(step.ScriptId));
        return action();
    });

    internal PreparedCaptureContext PrepareCapture(StepEvidenceBinding primary) =>
        _root.Use(() => Publish(() =>
    {
        var state = ReadSynchronized();
        var target = state.Total.Steps.Single(step => step.StepId == _stepId).Target?.Android ?? throw Invalid();
        var startup = AndroidStartupCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
            .SingleOrDefault(value => value.Binding.StepId == _stepId) ?? throw Invalid();
        if (_published is not null || _startup != startup.Artifact || target.Driver is not ("genericAdb" or "mumu") ||
            primary.StepId != _stepId || primary.ParentJournalId != _journalId || primary.ParentOperationId != _operationId ||
            primary.InstanceId != _instance.InstanceId || primary.AttemptId.ToString("D") != state.Attempt.AttemptId ||
            primary.RunId.ToString("D") != state.Attempt.RunId ||
            startup.Outcome is not (TargetExecutionOutcome.ControlledReady or TargetExecutionOutcome.Skipped))
            throw Invalid();
        if (_capture is not null)
        {
            if (_capture.Primary != primary) throw Invalid();
            return _capture;
        }
        _ = new AndroidResultEvidence(_instance, _database, _contracts).RequireStartup(state, startup, verifyContent: true);
        var facts = new AndroidCaptureFacts(startup.Binding, startup.Artifact, target.Endpoint, target.Package);
        if (target.Driver == "mumu")
        {
            var mapping = target.Mapping ?? throw Invalid();
            facts = facts with { MuMu = new(mapping.Index, mapping.EndpointPort,
                new(mapping.InfoEvidence.RelativePath, mapping.InfoEvidence.SizeBytes, mapping.InfoEvidence.Sha256), mapping.ManagerImageSha256) };
            _capture = new(this, primary, facts, target.Tool, state.Total.CaptureRuntime?.Broker ?? throw Invalid(), target.ScreenshotTool);
        }
        else _capture = new(this, primary, facts, target.Tool);
        return _capture;
    }));

    internal T UseCapture<T>(PreparedCaptureContext source, Func<T> action)
    {
        return _root.Use(() =>
        {
            RequireLive();
            if (!ReferenceEquals(_capture, source) || _published is not null) throw Invalid();
            var step = _prepared.Total.Plan.ReadWire().Steps.Single(value => value.StepId == _stepId);
            if (step.Target?.Android?.Emulator is { } emulator)
                (_prepared.Emulators ?? throw new IOException("emulator_runtime_owner_unavailable")).RequireActivation(emulator, Guid.Parse(step.ScriptId));
            return action();
        });
    }

    private AndroidControlBinding ControlBinding(AttemptWriterState state) =>
        new(_instance.InstanceId, _journalId, _operationId, Guid.Parse(state.Attempt.AttemptId), _stepId, state.Attempt.ExecutionPlan.Sha256);

    private T Publish<T>(Func<T> action) => _lease.PublishExecution(() =>
    {
        using var paths = DirectoryAttributeScope.Hold(_instance);
        using var blobs = BlobReadScope.Hold(_instance);
        return action();
    });

    private AttemptWriterState ReadSynchronized()
    {
        RequireLive();
        var state = AttemptWriterState.Read(_instance, _database, _contracts, _journalId);
        if (!state.IsExecutionStart || state.Journal.OperationId != _operationId) throw Invalid();
        if (state.Latest.Revision != state.Journal.Revision)
        {
            ExecutionAttemptProjection.Synchronize(_instance, _database, _contracts, state);
            state = AttemptWriterState.Read(_instance, _database, _contracts, _journalId);
        }
        return state;
    }

    private IReadOnlyList<AndroidCommandRegistration> Registrations(AttemptWriterState state) =>
        AndroidControlCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
            .Where(item => item.Binding.StepId == _stepId).ToArray();

    private AndroidCommandRegistration Required(AttemptWriterState state, ExecutionTargetBinding binding) =>
        Registrations(state).SingleOrDefault(item => item.Binding == binding) ?? throw Invalid();

    private BlobReference Persist<T>(T value)
    {
        var reference = Blobs.PersistPlan(BoundedJson.Serialize(value, StepResultCodec.Options));
        _held.Add(Blobs.OpenVerified(reference)); return reference;
    }
    private AndroidControlEvidence Reader() => new(_instance, _database, _contracts);
    internal static AndroidCommandRequest Copy(AndroidCommandRequest request) =>
        JsonSerializer.Deserialize<AndroidCommandRequest>(BoundedJson.Serialize(request, StepResultCodec.Options),
            StepResultCodec.Options) ?? throw Invalid();

    public void Dispose()
    {
        if (_published is null) throw new IOException("execution_android_control_not_published");
        for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
        _root.Dispose();
    }
    private static IOException Invalid() => new("execution_android_command_owner_unbound");
}
