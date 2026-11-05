using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal static class ExecutionTargetRecovery
{
    internal static void Reconcile(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, Guid journalId) => lease.PublishExecution(() =>
    {
        lease.VerifyHeld(instance);
        var state = AttemptWriterState.Read(instance, database, contracts, journalId);
        var pending = TargetJobStore.ReadJobs(state).Where(item => item.Stage is not ("stopped" or "retained" or "recoveredStopped" or "recoveredRetained")).ToArray();
        if (pending.Length == 0) return true;
        if (!state.IsExecutionStart || !state.IsDispatchClosed || state.Gate.State != EffectGateState.Entered ||
            state.Latest.Phase is not (JournalPhase.Applying or JournalPhase.Quarantined)) throw new IOException("execution_target_recovery_closed");
        AttemptTerminalEvidence.RequireStopped(database, journalId);
        if (state.Journal.Revision != state.Latest.Revision)
        {
            ExecutionAttemptProjection.Synchronize(instance, database, contracts, state);
            state = AttemptWriterState.Read(instance, database, contracts, journalId);
        }
        foreach (var item in pending)
        {
            state = AttemptWriterState.Read(instance, database, contracts, journalId);
            var current = TargetJobStore.Required(state, item.Binding);
            var evidence = new TargetJobEvidence(instance, database, contracts);
            _ = evidence.Require(current, state.Total, state.Latest.Root);
            var creator = TargetRootFacts.Creator(database, item.Binding);
            var root = TargetRootFacts.Read(database, item.Binding);
            var frozen = state.Total.Steps.Single(step => step.StepId == item.Binding.StepId).Target!.Pc!.ReadyImage;
            PcTargetImage? retainedImage = current.Stage == "retentionPrepared" ?
                new(frozen.Path, frozen.SizeBytes, frozen.Sha256, new(frozen.Identity.VolumeSerial, frozen.Identity.FileId)) : null;
            using var native = PreparedRecoveryObservation.Capture(item.Binding.JobName, item.Binding.SessionId, creator, root, retainedImage);
            native.Use(item.Binding.JobName, item.Binding.SessionId, creator, root, observation =>
            {
                var retained = observation.Kind == "retained";
                if (retained && (current.Stage != "retentionPrepared" ||
                    TargetCleanupFacts.Read(state, item.Binding.StepId).Application != TargetCleanupAction.RetainOwned))
                    throw new IOException("execution_target_retention_unbound");
                var document = new TargetRecoveryDocument("nexus.execution.target-recovery", 1, item.Binding, creator, root,
                    current.Stage == "retentionPrepared" ? current.Evidence : null, observation, DateTimeOffset.UtcNow);
                if (!retained) evidence.RequireStoppedFamily(document);
                return TargetJournalPublisher.Publish(instance, database, contracts, state, item.Binding,
                    retained ? "recoveredRetained" : "recoveredStopped", document);
            });
        }
        return true;
    });
}
