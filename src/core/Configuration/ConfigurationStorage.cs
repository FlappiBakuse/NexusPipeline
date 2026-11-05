using NexusPipeline.Core.Operations;

namespace NexusPipeline.Core.Configuration;

public interface IConfigurationRevisionStore
{
    ConfigurationRevision? Read(ConfigurationAddress address);
    ConfigurationSaveResult? Lookup(OperationIdentity operation, ConfigurationRevisionChange change);
    ConfigurationSaveResult Commit(OperationIdentity operation, ConfigurationRevisionChange change, DateTimeOffset acceptedAt);
}
