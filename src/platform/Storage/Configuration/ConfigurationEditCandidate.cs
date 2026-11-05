using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

internal sealed class ConfigurationEditCandidate(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal ConfigurationEditCompletion Read(ConfigurationEditStatus session, long expectedSessionRevision, AdapterEditHook? adapter = null)
    {
        var changes = session.Resources.Select(resource =>
        {
            var binding = Binding(resource);
            var tree = binding.Prepared?.Plan.Candidate.Snapshot ?? binding.Plan.Candidate.Snapshot;
            if (binding.Plan.EditPolicy?.Adapter is { } rule)
                tree = (adapter ?? throw new ConfigurationException("configuration_edit_adapter_unavailable"))
                    .Apply(session.SessionId, resource.Address, resource.ExpectedRevision, tree, rule, "editCommit");
            return new ConfigurationRevisionChange(resource.Address, resource.ExpectedRevision, session.ExpectedBindingRevision,
                resource.Contract, tree, ConfigurationUpdateKind.Manual);
        }).ToImmutableArray();
        return new(session.SessionId, expectedSessionRevision, new(changes));
    }

    internal void Validate(ConfigurationEditStatus session, ConfigurationEditCompletion completion)
    {
        var blobs = new ImmutableBlobStore(instance, contracts);
        foreach (var resource in session.Resources)
        {
            var policy = Binding(resource).Plan.EditPolicy ?? throw new IOException("configuration_editor_write_policy_required");
            var tree = completion.Batch.Changes.Single(change => change.Address == resource.Address).Tree;
            new ConfigurationContentValidation(blobs).Require(session.SessionId, resource.Address, resource.ExpectedRevision, policy, tree);
        }
    }

    private BoundConfigurationJournal Binding(ConfigurationEditResource resource) => ConfigurationJournalBinding.Read(instance,
        new JournalStore(instance, contracts).Read(resource.JournalId), contracts, database);
}
