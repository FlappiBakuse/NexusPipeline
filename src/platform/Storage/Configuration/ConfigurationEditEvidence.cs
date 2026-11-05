using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

internal sealed class ConfigurationEditEvidence(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal IDisposable Hold(ConfigurationEditStatus session, bool installed, ConfigurationRevisionBatch? save = null) =>
        new ConfigurationSceneEvidence(instance, database, contracts).Hold(session.Resources.Select(resource => new ConfigurationSceneResource(session.SessionId,
            resource.JournalId, JournalKind.ConfigEdit, resource.Address, resource.PlanPath, resource.PlanDigest, resource.ExpectedRevision)).ToArray(), installed,
            save?.Changes.ToDictionary(item => item.Address, item => item.Tree));
}
