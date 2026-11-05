using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal static class ProviderLauncherRecovery
{
    internal static void Reconcile(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, Guid journalId) => lease.PublishExecution(() =>
    {
        var state = AttemptWriterState.Read(instance, database, contracts, journalId);
        var pending = ProviderLauncherCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
            .Where(item => item.Stage is not ("stopped" or "retained" or "recoveredStopped" or "recoveredRetained")).ToArray();
        if (pending.Length == 0) return true;
        if (!state.IsExecutionStart || !state.IsDispatchClosed || state.Gate.State != EffectGateState.Entered ||
            state.Latest.Phase is not (JournalPhase.Applying or JournalPhase.Quarantined)) throw Invalid();
        AttemptTerminalEvidence.RequireStopped(database, journalId);
        foreach (var item in pending)
        {
            state = AttemptWriterState.Read(instance, database, contracts, journalId);
            if (state.Latest.Revision != state.Journal.Revision)
            { ExecutionAttemptProjection.Synchronize(instance, database, contracts, state); state = AttemptWriterState.Read(instance, database, contracts, journalId); }
            var current = ProviderLauncherCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256).Single(job => job.Binding == item.Binding);
            var creator = ProviderLauncherRows.Creator(database, item.Binding);
            var root = ProviderLauncherRows.Root(database, item.Binding);
            var image = state.Total.Steps.Single(step => step.StepId == item.Binding.StepId).Provider!.Launcher!.Image;
            var prepared = current.Stage == "retentionPrepared";
            PcTargetImage? allowed = prepared ? new(image.Path, image.SizeBytes, image.Sha256,
                new(image.Identity.VolumeSerial, image.Identity.FileId)) : null;
            using var observed = PreparedRecoveryObservation.Capture(item.Binding.JobName, item.Binding.SessionId, creator, root, allowed);
            observed.Use(item.Binding.JobName, item.Binding.SessionId, creator, root, observation =>
            {
                if (prepared && observation.Kind == "missing") throw Invalid();
                if (observation.Kind == "retained")
                {
                    if (!prepared || root is null || observation.LivingFamily.Any(member => member.UserSid != root.UserSid || member.IsElevated != root.IsElevated))
                        throw Invalid();
                }
                if (prepared && observation.Kind == "empty")
                {
                    var retention = new TargetJobEvidence(instance, database, contracts).Read<ProviderLauncherRetention>(current.Artifact!);
                    if (retention.Inventory.AllFamily.Any(member => WriterStopEvidence.ObserveProcess(member) != WriterState.Stopped)) throw Invalid();
                }
                var previous = ProviderLauncherCodec.ReadAll(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
                    .Any(job => job.Binding == item.Binding && job.Stage == "started");
                var document = new ProviderLauncherRecovered("nexus.execution.provider-launcher-recovered", 1, item.Binding,
                    creator, root, prepared ? current.Artifact : null, observation, state.Journal.Revision);
                var retained = observation.Kind == "retained";
                TargetJournalPublisher.PublishParticipant(instance, database, contracts, state, document, artifact =>
                    ProviderLauncherCodec.Participant(new(ProviderLauncherCodec.Format, 1, item.Binding,
                        retained ? "recoveredRetained" : "recoveredStopped", artifact, retained ? null :
                            previous ? EngineOutcome.Succeeded : EngineOutcome.Unknown), state.Total.Steps.Single(step => step.StepId == item.Binding.StepId).ScriptId));
                return true;
            });
        }
        return true;
    });

    private static IOException Invalid() => new("execution_provider_launcher_recovery_unverified");
}
