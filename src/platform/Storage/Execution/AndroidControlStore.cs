using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class AndroidControlStore : IDisposable
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
            TargetRootFacts.InsertCommandInside(connection, transaction, state, registration, request, identity, _clock);
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

    internal bool IsEmulatorLaunch(AndroidCommandRequest request) => request.Action == ExecutionAndroidAction.LaunchDevice &&
        _prepared.Total.Plan.ReadWire().Steps.Single(step => step.StepId == request.Binding.StepId).Target?.Android?.Emulator is not null;

    internal PreparedAndroidRetention PrepareRetention(PreparedAndroidAuthority authority, OwnedWorkerJob job) => authority.Use(job, request =>
        _root.Use(() => _lease.PublishExecution(() =>
        {
            var state = ReadSynchronized(); var inventory = job.CaptureTargetInventory();
            RequireRetention(state, request, inventory);
            var root = ReadRegistrationRoot(state, request) ?? throw new IOException("execution_emulator_retention_root_missing");
            var primary = StepResultCodec.Read(state.Latest.Root, state.Total).Single(value => value.Binding.StepId == request.Binding.StepId).Latest.Artifact;
            var document = new AndroidLaunchRetention("nexus.execution.android-launch-retention", 1, request.Binding,
                AndroidCommandIdentity.Digest(request), root, inventory, primary);
            var reference = PublishRetention(state, request, AndroidRetentionCodec.Prepared, document);
            return new PreparedAndroidRetention(this, authority, job, reference);
        })));

    internal T UseRetention<T>(AndroidCommandRequest request, BlobReference reference, OwnedTargetInventory inventory, Func<T> effect) =>
        _root.Use(() => _lease.PublishExecution(() =>
        {
            var state = ReadSynchronized(); RequireRetention(state, request, inventory);
            var document = Reader().Read<AndroidLaunchRetention>(reference);
            if (document.Binding != request.Binding || document.RequestDigest != AndroidCommandIdentity.Digest(request) ||
                document.Root != ReadRegistrationRoot(state, request) || !AndroidRetentionEvidence.Same(document.Inventory, inventory) ||
                AndroidRetentionEvidence.Last(state.Latest.Root, request.Binding, AndroidRetentionCodec.Prepared) != reference) throw new IOException("execution_emulator_retention_changed");
            return effect();
        }));

    internal BlobReference RecordRetained(PreparedAndroidAuthority authority, OwnedWorkerJob job, PreparedAndroidRetention intent,
        OwnedTargetHandoff native) => authority.Use(job, request => job.UseTargetHandoff(native, () =>
        _root.Use(() => _lease.PublishExecution(() =>
        {
            var state = ReadSynchronized(); var prepared = Reader().Read<AndroidLaunchRetention>(intent.Reference);
            if (prepared.Binding != request.Binding || AndroidRetentionEvidence.Last(state.Latest.Root, request.Binding, AndroidRetentionCodec.Prepared) != intent.Reference ||
                !AndroidRetentionEvidence.Same(prepared.Inventory, native.Inventory) || native.PreviousLimitFlags != 0x00002000 || native.EffectiveLimitFlags != 0)
                throw new IOException("execution_emulator_handoff_unconfirmed");
            return PublishRetention(state, request, AndroidRetentionCodec.Retained, new AndroidLaunchRetained("nexus.execution.android-launch-retained", 1,
                request.Binding, intent.Reference, native));
        }))));

    private void RequireRetention(AttemptWriterState state, AndroidCommandRequest request, OwnedTargetInventory inventory)
    {
        _ = Required(state, request.Binding); AndroidControlAdmission.RequireCollectedMain(state, request.Binding.StepId);
        if (!IsEmulatorLaunch(request) || TargetCleanupFacts.Read(state, request.Binding.StepId).Device != TargetCleanupAction.None ||
            inventory.LimitFlags != 0x00002000 || inventory.ActiveProcesses == 0 || inventory.LivingFamily.Length != inventory.ActiveProcesses ||
            inventory.TotalProcesses != inventory.RetainedKernelObjects || inventory.AllFamily.All(value => value != ReadRegistrationRoot(state, request)))
            throw new IOException("execution_emulator_retention_not_authorized");
        TargetJobEvidence.RequireInventory(inventory);
    }

    // ReadRoot publishes on its own, and a publication callback cannot enter the publication queue again.
    private ProcessIdentity? ReadRegistrationRoot(AttemptWriterState state, AndroidCommandRequest request) =>
        TargetRootFacts.ReadCommand(_database, Required(state, request.Binding));

    private BlobReference PublishRetention<T>(AttemptWriterState state, AndroidCommandRequest request, string stage, T document) =>
        TargetJournalPublisher.PublishParticipant(_instance, _database, _contracts, state, document, reference =>
            AndroidRetentionCodec.Participant(new(AndroidRetentionCodec.Format, 1, request.Binding, stage, reference),
                state.Total.Steps.Single(step => step.StepId == request.Binding.StepId).ScriptId));

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

internal sealed record AndroidLaunchRetention(string Format, int Version, ExecutionTargetBinding Binding,
    string RequestDigest, ProcessIdentity Root, OwnedTargetInventory Inventory, BlobReference Primary);
internal sealed record AndroidLaunchRetained(string Format, int Version, ExecutionTargetBinding Binding,
    BlobReference Intent, OwnedTargetHandoff Native);

[SupportedOSPlatform("windows")]
internal sealed class PreparedAndroidRetention(AndroidControlStore owner, PreparedAndroidAuthority authority,
    OwnedWorkerJob job, BlobReference reference)
{
    internal BlobReference Reference => reference;
    internal T Use<T>(OwnedWorkerJob actual, OwnedTargetInventory inventory, Func<T> effect)
    {
        if (!ReferenceEquals(actual, job)) throw new IOException("execution_emulator_retention_owner_unbound");
        return authority.Use(actual, request => owner.UseRetention(request, reference, inventory, effect));
    }
}

internal static class AndroidControlRows
{
    internal static void RegisterInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, AndroidCommandRegistration item, AndroidCommandRequest request)
    {
        AndroidControlAdmission.RequireInside(connection, transaction, state, request);
        if (item.Binding != request.Binding || item.Sequence != request.Sequence ||
            item.RequestDigest != AndroidCommandIdentity.Digest(request)) throw Invalid();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO main.execution_target_command_jobs(journal_id,command_id,step_id,command_sequence,job_name,writer_id,document_json) " +
            "VALUES($journal,$id,$step,$sequence,$job,$writer,$document) ON CONFLICT(journal_id,command_id) DO NOTHING;";
        Bind(command, item);
        command.Parameters.AddWithValue("$step", item.Binding.StepId); command.Parameters.AddWithValue("$sequence", item.Sequence);
        command.Parameters.AddWithValue("$job", item.Binding.JobName); command.Parameters.AddWithValue("$writer", item.Binding.OwnerWriterId.ToString("D"));
        command.Parameters.AddWithValue("$document", System.Text.Encoding.UTF8.GetString(
            NexusPipeline.Platform.Serialization.BoundedJson.Serialize(item, StepResultCodec.Options)));
        command.ExecuteNonQuery();
        RequireRegisteredInside(connection, transaction, item);
    }

    internal static void RequireRegisteredInside(SqliteConnection connection, SqliteTransaction transaction, AndroidCommandRegistration item)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT step_id,command_sequence,job_name,writer_id,document_json FROM main.execution_target_command_jobs " +
            "WHERE journal_id=$journal AND command_id=$id;";
        Bind(command, item); using var row = command.ExecuteReader();
        if (!row.Read() || row.GetString(0) != item.Binding.StepId || row.GetInt32(1) != item.Sequence ||
            row.GetString(2) != item.Binding.JobName || row.GetString(3) != item.Binding.OwnerWriterId.ToString("D")) throw Invalid();
        var saved = AndroidControlCodec.Decode<AndroidCommandRegistration>(System.Text.Encoding.UTF8.GetBytes(row.GetString(4)));
        if (saved != item) throw Invalid();
    }

    internal static void VerifyInside(SqliteConnection connection, SqliteTransaction transaction,
        Guid journalId, IReadOnlyList<AndroidCommandRegistration> projected)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT command_id FROM main.execution_target_command_jobs WHERE journal_id=$journal LIMIT 1025;";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        var ids = new List<Guid>();
        using (var row = command.ExecuteReader()) while (row.Read()) ids.Add(Guid.Parse(row.GetString(0)));
        if (ids.Count != projected.Count || !ids.ToHashSet().SetEquals(projected.Select(item => item.Binding.TargetId))) throw Invalid();
        foreach (var item in projected) RequireRegisteredInside(connection, transaction, item);
    }

    private static void Bind(SqliteCommand command, AndroidCommandRegistration item)
    {
        command.Parameters.AddWithValue("$journal", item.Binding.ParentJournalId.ToString("D"));
        command.Parameters.AddWithValue("$id", item.Binding.TargetId.ToString("D"));
    }

    private static IOException Invalid() => new("execution_android_command_sql_unbound");
}

internal static class AndroidControlAdmission
{
    internal static bool IsCleanup(AndroidCommandPurpose purpose) => purpose is
        AndroidCommandPurpose.CleanupReadiness or AndroidCommandPurpose.CleanupApplication or
        AndroidCommandPurpose.ShutdownDevice or AndroidCommandPurpose.ConfirmOffline;

    internal static void RequireInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, AndroidCommandRequest request)
    {
        AttemptWriterState.RequireCursor(connection, transaction, state.Journal, EffectGateState.Entered);
        if (!state.IsExecutionStart || state.Journal.Phase != JournalPhase.Applying ||
            state.Latest.Revision != state.Journal.Revision || request.Binding.ParentJournalId != state.Journal.JournalId)
            throw Invalid();
        var actualSubject = SubjectInside(connection, transaction, state);
        var expected = AndroidCommandIdentity.Derive(state.Total, request.Binding, actualSubject,
            request.Sequence, request.Purpose, request.Action);
        if (AndroidCommandIdentity.Digest(request) != AndroidCommandIdentity.Digest(expected)) throw Invalid();
        if (!IsCleanup(request.Purpose))
        {
            ExecutionCancellationRows.RequireDispatchOpen(connection, transaction, state.Journal.JournalId);
            PcStageProjection.RequireConfigurationInstalledInside(connection, transaction, state, request.Binding.StepId);
            ExecutionStepAdmission.RequireOpenRoles(state.Total, state.Jobs, request.Binding.StepId, "process", state.Latest.Root);
            var process = state.Jobs.Single(job => job.StepId == request.Binding.StepId && job.Role == "process");
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT NOT EXISTS(SELECT 1 FROM main.journal_writer_job_roots WHERE journal_id=$journal AND job_name=$job);";
            command.Parameters.AddWithValue("$journal", state.Journal.JournalId.ToString("D"));
            command.Parameters.AddWithValue("$job", process.JobName);
            if ((long)command.ExecuteScalar()! != 1 || state.Coverage.Any(item =>
                    item.StepId == request.Binding.StepId && item.Kind == StepCompletionKind.Collected)) throw Invalid();
            ExecutionStepAdmission.RequirePredecessorsInside(connection, transaction, state, state.Latest,
                state.Total.Steps.ToList().FindIndex(step => step.StepId == request.Binding.StepId));
            return;
        }
        RequireCollectedMain(state, request.Binding.StepId);
        var cleanup = TargetCleanupFacts.Read(state, request.Binding.StepId);
        if (request.Purpose == AndroidCommandPurpose.CleanupReadiness &&
                (request.Driver != "mumu" || cleanup.Application == TargetCleanupAction.None && cleanup.Device == TargetCleanupAction.None) ||
            request.Purpose == AndroidCommandPurpose.CleanupApplication &&
                cleanup.Application != TargetCleanupAction.StopAndroidApplication ||
            request.Purpose is AndroidCommandPurpose.ShutdownDevice or AndroidCommandPurpose.ConfirmOffline &&
                cleanup.Device != TargetCleanupAction.ShutdownAndroid) throw Invalid();
        var accepted = ExecutionCancellationRows.Read(connection, transaction, state.Journal, state.Attempt);
        if (accepted?.Identity != state.Cancellation?.Identity) throw Invalid();
    }

    internal static void RequireCollectedMain(AttemptWriterState state, string stepId)
    {
        var result = StepResultCodec.Read(state.Latest.Root, state.Total).SingleOrDefault(item => item.Binding.StepId == stepId);
        if (result?.IsCollected != true ||
            state.Jobs.Any(job => job.StepId == stepId && (job.Role == "postHook" || job.ClosedElapsedMilliseconds is null)))
            throw new IOException("execution_android_necessary_collection_pending");
    }

    internal static string SubjectInside(SqliteConnection connection, SqliteTransaction transaction, AttemptWriterState state)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT subject_id FROM main.operations WHERE operation_id=$operation AND operation_kind='execution.start' AND initial_journal_id=$journal;";
        command.Parameters.AddWithValue("$operation", state.Journal.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$journal", state.Journal.JournalId.ToString("D"));
        return command.ExecuteScalar() as string ?? throw Invalid();
    }

    private static IOException Invalid() => new("execution_android_command_admission_closed");
}

internal sealed class AndroidControlClock(ExecutionTimeBudget runBudget, TimeSpan cleanupLimit)
{
    private long? _cleanupStarted;

    internal TimeSpan Remaining(bool cleanup)
    {
        if (!cleanup) return runBudget.Read().Remaining;
        if (cleanupLimit <= TimeSpan.Zero || cleanupLimit > TimeSpan.FromMinutes(1))
            throw new IOException("execution_android_cleanup_budget_invalid");
        _cleanupStarted ??= Stopwatch.GetTimestamp();
        var remaining = cleanupLimit - Stopwatch.GetElapsedTime(_cleanupStarted.Value);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    internal void Require(bool cleanup)
    {
        if (Remaining(cleanup) == TimeSpan.Zero)
            throw new IOException(cleanup ? "execution_android_cleanup_deadline" : "execution_total_budget_exhausted");
    }
}
