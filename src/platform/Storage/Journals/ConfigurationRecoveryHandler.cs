using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Configuration;

namespace NexusPipeline.Platform.Storage.Journals;

public sealed class ConfigurationRecoveryHandler : IJournalRecoveryHandler
{
    private readonly RuntimeInstance _instance;
    private readonly ContractReader _contracts;
    private readonly StateDatabase _database;
    private readonly Func<Guid, ConfigurationWriterObservation> _writers;
    private readonly InstanceLease? _executionLease;
    public JournalKind Kind { get; }

    public ConfigurationRecoveryHandler(RuntimeInstance instance, ContractReader contracts, StateDatabase database, JournalKind kind,
        Func<Guid, ConfigurationWriterObservation> writers, InstanceLease? executionLease = null)
    {
        if (kind is not (JournalKind.ConfigExchange or JournalKind.ConfigEdit)) throw new ArgumentException("configuration_recovery_kind_invalid");
        _instance = instance; _contracts = contracts; _database = database; _writers = writers; Kind = kind;
        _executionLease = executionLease;
    }

    public IJournalRecoveryWork Prepare(RegisteredJournal journal)
    {
        if (journal.Kind != Kind) throw new InvalidOperationException("configuration_recovery_handler_unbound");
        var frozen = journal with { ResourceKeys = Array.AsReadOnly(journal.ResourceKeys.ToArray()) };
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(_instance.RuntimeRoot, "journals", journal.JournalId.ToString("D")));
        var initial = new InitialConfigurationJournal(_instance, _contracts, _database);
        if (Directory.Exists(path)) _ = CreateSession(journal.JournalId);
        else initial.VerifyPreparedPlan(journal);
        var parent = RunRecoveryBinding.Read(_instance, _database, _contracts, journal.JournalId)?.ParentJournalId;
        return new Work(() =>
        {
            initial.MaterializeIfAbsent(frozen);
            if (_executionLease is not null)
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                new EditOutputRecorder(_instance, _database, _contracts, _executionLease).SealRecoveredRun(frozen.JournalId);
            }
            return CreateSession(frozen.JournalId);
        }, parent is { } id ? [id] : []);
    }

    private ConfigurationRecoverySession CreateSession(Guid id) => new(_instance, id, _contracts, _database, () => _writers(id));

    private sealed class Work(Func<ConfigurationRecoverySession> open, IReadOnlyList<Guid> dependencies) : IJournalRecoveryWork
    {
        public IReadOnlyList<Guid> MustRunAfter => dependencies;
        public JournalRecoveryResult Execute() => new ConfigurationRecovery(open()).Recover() switch
        {
            ConfigurationRecoveryResult.NotExecuted => JournalRecoveryResult.NotExecuted,
            ConfigurationRecoveryResult.Restored => JournalRecoveryResult.Restored,
            ConfigurationRecoveryResult.Quarantined => JournalRecoveryResult.Quarantined,
            ConfigurationRecoveryResult.AlreadyCompleted => JournalRecoveryResult.AlreadyCompleted,
            _ => throw new InvalidOperationException("configuration_recovery_result_unknown")
        };
    }
}
