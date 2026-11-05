using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class ProviderTerminalStore(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease)
{
    internal void Record(PreparedProviderTerminal proof)
    {
        lease.RequirePublicationEntry();
        proof.Use(proof.Binding, (artifact, decision) => lease.PublishExecution(() =>
        {
            lease.VerifyHeld(instance);
            var state = AttemptWriterState.Read(instance, database, contracts, proof.Binding.ParentJournalId);
            var prior = ProviderTerminalCodec.Read(state.Latest.Root, state.Total)
                .SingleOrDefault(item => item.Binding.StepId == proof.Binding.StepId);
            if (prior is not null)
            {
                if (prior.Binding != proof.Binding || prior.Artifact != artifact || prior.Decision != decision)
                    throw new IOException("execution_provider_terminal_changed");
                ExecutionAttemptProjection.Synchronize(instance, database, contracts, state);
                return true;
            }
            if (state.Journal.Phase != JournalPhase.Applying || state.Gate.State != EffectGateState.Entered ||
                state.Latest.Revision != state.Journal.Revision || state.IsDispatchClosed)
                throw new IOException("execution_provider_admission_closed");
            var step = state.Total.Steps.Single(step => step.StepId == proof.Binding.StepId);
            var provider = step.Provider ?? throw new IOException("execution_provider_terminal_unbound");
            var replay = new ProviderControlReader(instance, database, contracts).Read(proof.Binding, artifact);
            if (replay.Terminal != decision) throw new IOException("execution_provider_terminal_unbound");
            var intended = new ProviderTerminalIntent(ProviderTerminalCodec.Format, 1, proof.Binding,
                Guid.Parse(provider.ActivationId), provider.EntryId, state.Journal.Revision, artifact, decision);
            TargetJournalPublisher.AppendParticipant(instance, database, contracts, state,
                ProviderTerminalCodec.Participant(intended, step.ScriptId));
            return true;
        }));
    }
}
