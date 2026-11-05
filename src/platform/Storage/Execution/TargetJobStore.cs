using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Execution;
using System.Collections.Immutable;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class TargetJobStore : IDisposable
{
    private readonly RuntimeInstance instance;
    private readonly StateDatabase database;
    private readonly ContractReader contracts;
    private readonly InstanceLease lease;
    private readonly ResourceOperationBorrow root;
    private readonly ITargetPersistenceObserver? observer;
    private readonly Guid journalId, operationId;

    private TargetJobStore(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        ResourceOperationBorrow root, Guid journalId, Guid operationId, ITargetPersistenceObserver? observer)
    { this.instance = instance; this.database = database; this.contracts = contracts; this.lease = lease;
        this.root = root; this.journalId = journalId; this.operationId = operationId; this.observer = observer; }

    internal static TargetJobStore ForAccepted(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, ResourceLeaseManager resources, AcceptedExecutionAdmission accepted, ITargetPersistenceObserver? observer = null)
    {
        lease.VerifyHeld(instance); accepted.Prepared.RequireLive();
        if (accepted.Root.OperationId != accepted.Identity.OperationId || accepted.Prepared.Total.Plan.ReadWire().InstanceId != instance.InstanceId.ToString("D"))
            throw new IOException("execution_target_resource_owner_unbound");
        var borrow = accepted.Root.Borrow(resources, accepted.Identity.OperationId, ExecutionPlanResources.Claims(accepted.Prepared.Total.Plan.ReadWire()));
        return new(instance, database, contracts, lease, borrow, accepted.Receipt.JournalId, accepted.Identity.OperationId, observer);
    }

    internal PreparedGameRole Register(OwnedWorkerJob actualJob, string stepId) => root.Use(() => lease.PublishExecution(() =>
    {
        var state = ReadSynchronized();
        var step = state.Total.Steps.Single(item => item.StepId == stepId);
        var existing = ReadJobs(state).SingleOrDefault(item => item.Binding.StepId == stepId);
        if (existing is not null)
        {
            if (existing.Stage != "registered" || existing.Binding.JobName != actualJob.Name ||
                existing.Binding.OwnerWriterId != JournalWriterStore.CurrentWriter.WriterId ||
                actualJob.GetAccounting() is not { ActiveProcesses: 0, TotalProcesses: 0 })
                throw new IOException("execution_target_registration_changed");
            return PreparedGameRole.Read(this, existing.Binding, actualJob);
        }
        if (state.IsDispatchClosed || state.Latest.Revision != state.Journal.Revision || state.Journal.Phase != JournalPhase.Applying ||
            state.Gate.State != EffectGateState.Entered || step.Target is not { Kind: "pc", LaunchByHost: true, Pc: not null } ||
            actualJob.GetAccounting() is not { ActiveProcesses: 0, TotalProcesses: 0 } || actualJob.ActiveProcessIds().Count != 0 ||
            ReadJobs(state).Any(item => item.Binding.StepId == stepId || item.Binding.JobName == actualJob.Name) ||
            state.Jobs.Any(item => item.JobName == actualJob.Name)) throw new IOException("execution_target_registration_closed");
        RequireBeforeMain(state, stepId);
        _ = new JournalWriterStore(database).RequireRegisteredExecutor(journalId);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var binding = new ExecutionTargetBinding(instance.InstanceId, journalId, state.Journal.OperationId,
            Guid.Parse(state.Attempt.AttemptId), stepId, Guid.NewGuid(), actualJob.Name, JournalWriterStore.CurrentWriter.WriterId,
            process.SessionId, state.Attempt.ExecutionPlan.Sha256);
        Append(state, new(TargetJobCodec.Format, 1, binding, "registered", null));
        return PreparedGameRole.Read(this, binding, actualJob);
    }));

    internal void RecordRoot(PreparedGameRole authority, OwnedWorkerJob actualJob, OwnedWorkerIdentity root) =>
        authority.Use(actualJob, authority.Binding, facts => lease.PublishExecution(() =>
        {
            var state = ReadSynchronized();
            var current = Required(state, facts.Binding);
            if (current.Stage == "root")
            {
                var saved = ReadRoot(facts.Binding).Root;
                if (saved != new ProcessIdentity(root.ProcessId, root.CreatedAtUtc, root.ImagePath, root.ImageSha256, root.UserSid, root.IsElevated))
                    throw new IOException("execution_target_root_changed");
                return true;
            }
            var admitted = TargetRootFacts.Read(database, facts.Binding);
            if (current.Stage != "registered" || state.IsDispatchClosed && admitted is null || root.InstanceId != instance.InstanceId ||
                root.AttemptId != facts.Binding.AttemptId || root.ImageSha256 != facts.Launch.ExecutableSha256 ||
                !StringComparer.OrdinalIgnoreCase.Equals(root.ImagePath, facts.Launch.ExecutablePath))
                throw new IOException("execution_target_start_closed");
            RequireBeforeMain(state, facts.Binding.StepId);
            var identity = new ProcessIdentity(root.ProcessId, root.CreatedAtUtc, root.ImagePath, root.ImageSha256, root.UserSid, root.IsElevated);
            using var process = System.Diagnostics.Process.GetProcessById(root.ProcessId);
            if (!actualJob.ContainsProcessHandle(process.SafeHandle) || ProcessIdentityReader.Read(root.ProcessId) != identity ||
                actualJob.GetAccounting() is not { ActiveProcesses: 1, TotalProcesses: 1 } ||
                !actualJob.ActiveProcessIds().SequenceEqual([root.ProcessId])) throw new IOException("execution_target_native_root_unbound");
            var document = new TargetRootDocument("nexus.execution.target-root", 1, facts.Binding, identity);
            database.Write((connection, transaction) =>
            {
                TargetRootFacts.InsertInside(connection, transaction, state, facts.Binding, identity);
                return true;
            });
            observer?.Reached(TargetPersistencePoint.RootAdmitted, facts.Binding);
            PublishArtifact(state, facts.Binding, "root", document);
            return true;
        }));

    internal TargetRootDocument ReadRoot(ExecutionTargetBinding binding)
    {
        var state = Read(binding.ParentJournalId);
        _ = Required(state, binding);
        var identity = TargetRootFacts.Read(database, binding) ?? throw new IOException("execution_target_root_missing");
        return new("nexus.execution.target-root", 1, binding, identity);
    }

    internal AttemptWriterState Read(Guid journalId)
    {
        lease.VerifyHeld(instance); root.VerifyHeld();
        if (journalId != this.journalId) throw new IOException("execution_target_attempt_unbound");
        var state = AttemptWriterState.Read(instance, database, contracts, journalId);
        if (!state.IsExecutionStart || state.Journal.OperationId != operationId) throw new IOException("execution_target_attempt_unbound");
        return state;
    }

    internal static IReadOnlyList<TargetJobState> ReadJobs(AttemptWriterState state) =>
        TargetJobCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);

    internal static TargetJobState Required(AttemptWriterState state, ExecutionTargetBinding binding) =>
        ReadJobs(state).SingleOrDefault(item => item.Binding == binding) ?? throw new IOException("execution_target_job_unbound");

    internal T ReadArtifact<T>(BlobReference reference) where T : class =>
        new TargetJobEvidence(instance, database, contracts).Read<T>(reference);

    internal GameRoleFacts RoleFacts(ExecutionTargetBinding binding)
    {
        var state = Read(binding.ParentJournalId);
        _ = Required(state, binding);
        var target = state.Total.Steps.Single(step => step.StepId == binding.StepId).Target;
        if (target is not { Kind: "pc", LaunchByHost: true, Pc: not null } || target.Android is not null) throw new IOException("execution_host_game_role_unbound");
        var pc = target.Pc;
        return new(binding, EditorCommandPolicy.Freeze(new(pc.Launch.Command.Path, pc.Launch.Command.Sha256,
            System.Collections.Immutable.ImmutableArray.CreateRange(pc.Launch.Arguments), pc.Launch.WorkingDirectory)),
            new(pc.ReadyImage.Path, pc.ReadyImage.SizeBytes, pc.ReadyImage.Sha256, new(pc.ReadyImage.Identity.VolumeSerial, pc.ReadyImage.Identity.FileId)),
            pc.ReadinessMode == "process" ? PcReadinessMode.Process : PcReadinessMode.VisibleWindow,
            new(NexusPipeline.Core.Execution.ExecutionTargetKind.Pc, true, target.ForceCloseAtEnd), target.WaitSeconds,
            pc.Launch.LaunchKind == "command" ? CommandLineSyntax.CommandInterpreter : CommandLineSyntax.ArgumentVector);
    }

    internal T Use<T>(Func<T> callback) => root.Use(() => { lease.VerifyHeld(instance); return callback(); });

    internal void RecordReady(PreparedGameRole authority, OwnedWorkerJob actualJob, PcTargetReadiness readiness) =>
        authority.Use(actualJob, authority.Binding, facts => readiness.UseOwned(actualJob, members => lease.PublishExecution(() =>
        {
            var state = ReadSynchronized();
            var current = Required(state, facts.Binding);
            var document = new TargetReadyDocument("nexus.execution.target-ready", 1, facts.Binding,
                facts.ReadinessMode.ToString(), members);
            if (current.Stage == "ready")
            {
                if (!Same(ReadArtifact<TargetReadyDocument>(current.Evidence!), document)) throw new IOException("execution_target_readiness_changed");
                return true;
            }
            if (current.Stage != "root" || state.IsDispatchClosed)
                throw new IOException("execution_target_readiness_closed");
            PublishArtifact(state, facts.Binding, "ready", document);
            return true;
        })));

    internal void RecordStopped(PreparedGameRole authority, OwnedWorkerJob actualJob, PreparedStoppedTarget proof) =>
        authority.Use(actualJob, authority.Binding, facts => proof.Use(actualJob, inventory => lease.PublishExecution(() =>
        {
            var state = ReadSynchronized();
            var current = Required(state, facts.Binding);
            if (current.Stage == "stopped")
            {
                var saved = ReadArtifact<TargetStoppedDocument>(current.Evidence!);
                if (saved.Binding != facts.Binding || !Same(saved.Inventory, inventory)) throw new IOException("execution_target_stop_changed");
                return true;
            }
            if (current.Stage is "retained" or "recoveredRetained") throw new IOException("execution_target_collection_closed");
            var targetRoot = TargetRootFacts.Read(database, facts.Binding);
            PublishArtifact(state, facts.Binding, "stopped", new TargetStoppedDocument("nexus.execution.target-stopped", 1, facts.Binding, targetRoot, inventory));
            return true;
        })));

    internal void RecordRetained(PreparedGameRole authority, OwnedWorkerJob actualJob, PreparedRetentionIntent intent, OwnedTargetHandoff handoff) =>
        authority.Use(actualJob, authority.Binding, facts => actualJob.UseTargetHandoff(handoff, () => lease.PublishExecution(() =>
        {
            var state = ReadSynchronized();
            var current = Required(state, facts.Binding);
            var document = new TargetRetainedDocument("nexus.execution.target-retained", 1, facts.Binding, intent.Reference, handoff);
            if (current.Stage == "retained")
            {
                if (!Same(ReadArtifact<TargetRetainedDocument>(current.Evidence!), document)) throw new IOException("execution_target_retention_changed");
                return true;
            }
            if (current.Stage != "retentionPrepared" || current.Evidence != intent.Reference)
                throw new IOException("execution_target_retention_changed");
            var prepared = ReadArtifact<TargetRetentionDocument>(intent.Reference);
            if (!Same(prepared.Inventory, handoff.Inventory) || handoff.PreviousLimitFlags != 0x00002000 || handoff.EffectiveLimitFlags != 0)
                throw new IOException("execution_target_native_handoff_unbound");
            PublishArtifact(state, facts.Binding, "retained", document);
            return true;
        })));

    internal PreparedRetentionIntent PrepareRetention(PreparedGameRole authority, OwnedWorkerJob actualJob, OwnedTargetInventory inventory) =>
        authority.Use(actualJob, authority.Binding, facts => lease.PublishExecution(() =>
        {
            var state = ReadSynchronized();
            var current = Required(state, facts.Binding);
            if (current.Stage is not ("root" or "ready" or "retentionPrepared")) throw new IOException("execution_target_retention_closed");
            RequireRetainDecision(state, facts);
            RequireRetainInventory(facts, inventory);
            var (closures, primary) = Necessary(state, facts.Binding.StepId);
            var originalStage = current.Stage;
            if (current.Stage == "retentionPrepared")
            {
                var previous = ReadArtifact<TargetRetentionDocument>(current.Evidence!);
                if (Same(previous.Inventory, inventory) && previous.NecessaryClosures.SequenceEqual(closures) && previous.PrimaryResult == primary)
                    return new PreparedRetentionIntent(this, authority, current.Evidence!, previous);
                originalStage = previous.TargetStage;
            }
            if (TargetJobCodec.ReadAll(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
                .Count(item => item.Binding == facts.Binding && item.Stage == "retentionPrepared") >= TargetJobCodec.MaximumRetentionIntents)
                throw new IOException("execution_target_retention_revision_limit");
            var document = new TargetRetentionDocument("nexus.execution.target-retention", 1, facts.Binding,
                ReadRoot(facts.Binding).Root, inventory, closures, primary, originalStage);
            var artifact = PublishArtifact(state, facts.Binding, "retentionPrepared", document);
            observer?.Reached(TargetPersistencePoint.RetentionPrepared, facts.Binding);
            return new PreparedRetentionIntent(this, authority, artifact, document);
        }));

    internal T UseRetention<T>(ExecutionTargetBinding binding, BlobReference reference, OwnedTargetInventory inventory, Func<T> effect) =>
        root.Use(() => lease.PublishExecution(() =>
        {
            var state = Read(binding.ParentJournalId);
            var current = Required(state, binding);
            if (current.Stage != "retentionPrepared" || current.Evidence != reference) throw new IOException("execution_target_retention_changed");
            var document = ReadArtifact<TargetRetentionDocument>(reference);
            if (document.Format != "nexus.execution.target-retention" || document.Version != 1 || document.Binding != binding ||
                !Same(document.Inventory, inventory) || document.Root != ReadRoot(binding).Root) throw new IOException("execution_target_retention_unbound");
            var facts = RoleFacts(binding);
            RequireRetainDecision(state, facts);
            RequireRetainInventory(facts, inventory);
            var necessary = Necessary(state, binding.StepId);
            if (!necessary.Closures.SequenceEqual(document.NecessaryClosures) || necessary.Primary != document.PrimaryResult)
                throw new IOException("execution_target_necessary_boundary_changed");
            var result = effect();
            observer?.Reached(TargetPersistencePoint.HandoffApplied, binding);
            return result;
        }));

    private static void RequireRetainInventory(GameRoleFacts facts, OwnedTargetInventory inventory)
    {
        TargetJobEvidence.RequireInventory(inventory);
        if (inventory.LimitFlags != 0x00002000 || inventory.ActiveProcesses == 0 ||
            inventory.ActiveProcesses != inventory.LivingFamily.Length || inventory.TotalProcesses != inventory.RetainedKernelObjects ||
            inventory.LivingFamily.Any(member => !StringComparer.OrdinalIgnoreCase.Equals(member.ImagePath, facts.ReadyImage.Path) ||
                member.ImageSha256 != facts.ReadyImage.Sha256)) throw new IOException("execution_target_retained_role_unproven");
    }

    private void RequireRetainDecision(AttemptWriterState state, GameRoleFacts facts)
    {
        if (TargetCleanupFacts.Read(state, facts.Binding.StepId).Application != TargetCleanupAction.RetainOwned)
            throw new IOException("execution_target_retention_not_authorized");
    }

    private (ImmutableArray<BlobReference> Closures, BlobReference? Primary) Necessary(AttemptWriterState state, string stepId)
    {
        var jobs = state.Jobs.Where(item => item.StepId == stepId).OrderBy(item => item.JobName, StringComparer.Ordinal).ToArray();
        var required = ExecutionBindingRoles.Required(state.Total.Steps.Single(step => step.StepId == stepId)).Where(role => role != "postHook");
        if (!jobs.Select(job => job.Role).ToHashSet(StringComparer.Ordinal).SetEquals(required))
            throw new IOException("execution_target_necessary_roles_unrepresented");
        if (jobs.Any(item => item.ClosedElapsedMilliseconds is null)) throw new IOException("execution_target_necessary_collection_pending");
        var primary = StepResultCodec.Read(state.Latest.Root, state.Total).SingleOrDefault(item => item.Binding.StepId == stepId);
        if (jobs.Any(job => job.Role == "process") && primary is not { IsCollected: true }) throw new IOException("execution_target_necessary_collection_pending");
        if (primary is null) throw new IOException("execution_target_necessary_collection_pending");
        _ = new StepEvidenceReader(instance, database, contracts).Read(primary.Binding, primary.Latest.Artifact, collected: true);
        var hooks = HookResultCodec.Read(state.Latest.Root, state.Total);
        var references = ImmutableArray.CreateBuilder<BlobReference>();
        foreach (var job in jobs)
        {
            var closure = new JobClosureStore(instance, database, contracts).ReadForExecutionEvidence(state.Journal.JournalId, job.WriterId, job.JobName, job.SessionId)
                ?? throw new IOException("writer_job_closure_missing");
            if (closure.RunElapsedMilliseconds != job.ClosedElapsedMilliseconds || closure.Family.Any(member =>
                WriterStopEvidence.ObserveProcess(member) != WriterState.Stopped) || ExecutionBindingRoles.IsHook(job.Role) &&
                !hooks.Any(hook => hook.StepId == stepId && hook.Role == job.Role)) throw new IOException("execution_target_necessary_stop_unconfirmed");
            references.Add(new StepEvidenceReader(instance, database, contracts).ClosureReference(StepResultCodec.Binding(state.Latest.Root, job)));
        }
        return (references.ToImmutable(), primary?.Latest.Artifact);
    }

    internal TargetJobState? CurrentForStage(string stepId) => root.Use(() => lease.PublishExecution(() =>
        ReadJobs(ReadSynchronized()).SingleOrDefault(item => item.Binding.StepId == stepId)));

    internal ExecutionTargetCleanup ReadCleanup(string stepId) => root.Use(() => lease.PublishExecution(() =>
    {
        var state = ReadSynchronized();
        var job = ReadJobs(state).Single(item => item.Binding.StepId == stepId);
        return TargetCleanupFacts.Read(state, job.Binding.StepId);
    }));

    internal void RecordStage(PreparedPcStage proof)
    {
        if (proof.Binding.ParentJournalId != journalId || proof.Binding.ParentOperationId != operationId)
            throw new IOException("execution_target_stage_owner_unbound");
        proof.Use(proof.Binding, document => root.Use(() => lease.PublishExecution(() =>
        {
            var state = ReadSynchronized();
            if (document.Binding.InstanceId != instance.InstanceId || document.Binding.AttemptId.ToString("D") != state.Attempt.AttemptId ||
                document.Binding.TotalPlanSha256 != state.Attempt.ExecutionPlan.Sha256) throw new IOException("execution_target_stage_owner_unbound");
            var previous = PcStageCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
                .SingleOrDefault(item => item.Binding.StepId == document.Binding.StepId);
            if (previous is not null)
            {
                if (!Same(ReadArtifact<PcStageDocument>(previous.Artifact), document))
                    throw new IOException("execution_target_stage_changed");
                return true;
            }
            if (state.Journal.Phase != JournalPhase.Applying || state.Gate.State != EffectGateState.Entered ||
                document.Outcome == TargetExecutionOutcome.StoppedByRequest && state.Cancellation?.ReasonCode != document.ReasonCode)
                throw new IOException("execution_target_stage_closed");
            if (document.Outcome == TargetExecutionOutcome.StoppedByRequest)
            {
                var main = state.Jobs.Single(job => job.StepId == document.Binding.StepId && job.Role == "process");
                var binding = StepResultCodec.Binding(state.Latest.Root, main);
                var reader = new StepEvidenceReader(instance, database, contracts);
                var result = StepResultCodec.Read(state.Latest.Root, state.Total).SingleOrDefault(item => item.Binding == binding);
                if (reader.Context(binding).Root is not null || result is not { IsCollected: true } ||
                    result.Latest.Engine != EngineOutcome.NotStarted || result.Latest.Business != BusinessOutcome.Unverified)
                    throw new IOException("execution_target_primary_was_dispatched");
                StepWritersEvidence.RequireStopped(instance, database, contracts, journalId,
                    state.Jobs.Where(job => job.StepId == document.Binding.StepId).ToArray());
            }
            else RequireBeforeMain(state, document.Binding.StepId);
            TargetJournalPublisher.PublishParticipant(instance, database, contracts, state, document, artifact =>
                PcStageCodec.Participant(new(PcStageCodec.Format, 1, document.Binding, document.Outcome, document.ReasonCode, artifact),
                    state.Total.Steps.Single(step => step.StepId == document.Binding.StepId).ScriptId));
            return true;
        })));
    }

    internal PreparedGameRole? FindRegistered(OwnedWorkerJob actualJob, string stepId) => root.Use(() => lease.PublishExecution(() =>
    {
        var state = ReadSynchronized();
        var current = ReadJobs(state).SingleOrDefault(item => item.Binding.StepId == stepId);
        if (current is null) return null;
        if (current.Binding.JobName != actualJob.Name || current.Binding.OwnerWriterId != JournalWriterStore.CurrentWriter.WriterId)
            throw new IOException("execution_target_native_owner_unbound");
        return PreparedGameRole.Read(this, current.Binding, actualJob);
    }));

    private AttemptWriterState ReadSynchronized()
    {
        var state = Read(journalId);
        if (state.Latest.Revision != state.Journal.Revision)
        {
            ExecutionAttemptProjection.Synchronize(instance, database, contracts, state);
            state = Read(journalId);
        }
        return state;
    }

    private void RequireBeforeMain(AttemptWriterState state, string stepId)
    {
        ExecutionStepAdmission.RequireOpenRoles(state.Total, state.Jobs, stepId, "process", state.Latest.Root);
        var main = state.Jobs.Single(job => job.StepId == stepId && job.Role == "process");
        var binding = StepResultCodec.Binding(state.Latest.Root, main);
        if (new StepEvidenceReader(instance, database, contracts).Context(binding).Root is not null ||
            StepResultCodec.Read(state.Latest.Root, state.Total).Any(result => result.Binding.StepId == stepId))
            throw new IOException("execution_target_launch_after_main");
    }

    private static bool Same<T>(T first, T second) => JsonElement.DeepEquals(JsonSerializer.SerializeToElement(first, StepResultCodec.Options),
        JsonSerializer.SerializeToElement(second, StepResultCodec.Options));

    private BlobReference PublishArtifact<T>(AttemptWriterState state, ExecutionTargetBinding binding, string stage, T document) =>
        TargetJournalPublisher.Publish(instance, database, contracts, state, binding, stage, document);

    private void Append(AttemptWriterState state, TargetJobIntent item) =>
        TargetJournalPublisher.Append(instance, database, contracts, state, item);

    public void Dispose()
    {
        var state = Read(journalId);
        if (ReadJobs(state).Any(item => item.Stage is not ("retained" or "stopped" or "recoveredStopped" or "recoveredRetained"))) throw new IOException("execution_target_collection_pending");
        root.Dispose();
    }
}

internal sealed record TargetRootDocument(string Format, int Version, ExecutionTargetBinding Binding, ProcessIdentity Root);
internal sealed record TargetReadyDocument(string Format, int Version, ExecutionTargetBinding Binding, string Mode, ImmutableArray<ProcessIdentity> Members);
internal sealed record TargetRetainedDocument(string Format, int Version, ExecutionTargetBinding Binding, BlobReference Intent, OwnedTargetHandoff Native);
