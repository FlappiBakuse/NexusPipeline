using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Journals;

public enum ConfigurationPersistencePoint { JournalFlushed, SqlProjectionCommitted }

public interface IConfigurationPersistenceObserver
{
    void Reached(ConfigurationPersistencePoint point, Guid journalId, long revision, JournalPhase phase);
}
