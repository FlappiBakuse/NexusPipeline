using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

internal sealed record RetryConfigurationInput(FileTreeSnapshot Tree, BlobReference Reference)
{
    internal static RetryConfigurationInput Read(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        RunResourceStatus resource, FrozenRunState source)
    {
        var physical = new JournalStore(instance, contracts).Read(resource.JournalId);
        if (physical.Root.GetProperty("participants").GetArrayLength() != 2)
            throw new IOException("configuration_retry_output_missing");
        var bound = ConfigurationJournalBinding.Read(instance, physical, contracts, database);
        var output = bound.Prepared ?? throw new IOException("configuration_retry_output_missing");
        var blobs = new ImmutableBlobStore(instance, contracts);
        ConfigurationOutputEvidence.Require(output, output.Plan.Candidate);
        var policy = bound.Plan.EditPolicy ?? throw new IOException("configuration_retry_output_policy_missing");
        new ConfigurationContentValidation(blobs).Require(resource.OperationId, resource.Address, source.Source.Revision,
            policy, output.Plan.Candidate.Snapshot);
        var tree = new ConfigurationTaskTransformation(blobs).Restore(resource.OperationId, source.Source,
            source.Tasks, output.Plan.Candidate.Snapshot);
        new ConfigurationContentValidation(blobs).Require(resource.OperationId, resource.Address, source.Source.Revision, policy, tree);
        var decision = ConfigurationSynchronizationPolicy.Evaluate(source.Source.Tree, tree, TimeSpan.Zero, true, ConfigurationSyncTrigger.Finalization);
        if (!decision.IsAllowed) throw new ConfigurationException(decision.ReasonCode);
        return new(tree, new ConfigurationSnapshotCodec(blobs).Write(tree));
    }
}
