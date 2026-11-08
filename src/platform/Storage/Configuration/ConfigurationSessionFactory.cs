using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Configuration;

[SupportedOSPlatform("windows")]
public sealed class ConfigurationSessionFactory(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    InstanceLease lease, RecoveryIndex recovery)
{
    internal void Begin(Guid sessionId, string subject, Guid parentJournalId, string stepId, PreparedConfigurationRun prepared)
    {
        var runs = new ConfigurationRunStore(instance, database, contracts);
        var identity = ConfigurationRunStore.BeginIdentity(sessionId, subject, parentJournalId, stepId, prepared);
        var session = runs.Begin(identity, parentJournalId, stepId, prepared, DateTimeOffset.UtcNow);
        var exchange = new ConfigurationEditExchange(instance, database, contracts);
        foreach (var resource in session.Resources) exchange.Materialize(resource.JournalId);
    }

    internal ConfigurationRunController Control(Guid sessionId, string subject, PreparedConfigurationRun prepared,
        IConfigurationWriterScope writer, PayloadApplyRoots payloadRoots) =>
        new(instance, database, contracts, lease, recovery, sessionId, subject, prepared, writer,
            deferRegistration: true, payloadRoots: payloadRoots);

    internal void Restore(Guid sessionId, string subject)
    {
        var session = new ConfigurationRunStore(instance, database, contracts).Read(sessionId, subject)
            ?? throw new IOException("configuration_run_missing");
        var exchange = new ConfigurationEditExchange(instance, database, contracts);
        foreach (var resource in session.Resources)
            if (exchange.Restore(resource.JournalId) == ConfigurationRecoveryResult.Quarantined)
                throw new IOException("execution_configuration_restoration_unverified");
    }
}
