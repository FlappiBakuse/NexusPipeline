using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Journals;

internal sealed class InitialConfigurationJournal(RuntimeInstance instance, ContractReader contracts, StateDatabase database)
{
    internal void VerifyPreparedPlan(RegisteredJournal journal)
    {
        if (database.InstanceId != instance.InstanceId || journal.Phase != JournalPhase.Prepared || journal.Revision != 1 ||
            journal.Kind is not (JournalKind.ConfigExchange or JournalKind.ConfigEdit))
            throw new IOException("missing_configuration_journal_not_initial");
        var blobs = new ImmutableBlobStore(instance, contracts);
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, journal.PlanPath));
        var reference = new BlobReference(journal.PlanPath, new FileInfo(path).Length, journal.PlanDigest);
        var plan = new ConfigurationPlanCodec(instance, contracts, blobs).Read(reference);
        if (plan.InstanceId != instance.InstanceId || plan.OperationId != journal.OperationId || plan.JournalId != journal.JournalId || plan.Kind != journal.Kind)
            throw new IOException("missing_configuration_journal_plan_unbound");
        var effects = new FileEffectOutbox(database).Read(journal.JournalId);
        if (effects.Any(item => item.State != FileEffectState.Pending)) throw new IOException("missing_configuration_journal_has_effect_receipts");
    }

    internal void MaterializeIfAbsent(RegisteredJournal journal)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "journals", journal.JournalId.ToString("D")));
        if (Directory.Exists(path)) return;
        VerifyPreparedPlan(journal);
        var current = new RecoveryJournalRegistry(database).ReadJournal(journal.JournalId);
        if (current.OperationId != journal.OperationId || current.Kind != journal.Kind || current.Phase != journal.Phase || current.Revision != journal.Revision ||
            current.PlanDigest != journal.PlanDigest || current.PlanPath != journal.PlanPath || !current.ResourceKeys.SequenceEqual(journal.ResourceKeys))
            throw new IOException("missing_configuration_journal_registration_changed");
        var document = new ExecutionJournal
        {
            Format = "nexus.execution.journal", Version = 1, InstanceId = instance.InstanceId.ToString("D"),
            JournalId = journal.JournalId.ToString("D"), OperationId = journal.OperationId.ToString("D"),
            OperationKind = journal.Kind == JournalKind.ConfigExchange ? "configExchange" : "configEdit",
            Phase = "prepared", Revision = 1, PlanPath = journal.PlanPath, PlanDigest = journal.PlanDigest,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"), ResourceKeys = journal.ResourceKeys,
            Participants = [], Receipts = []
        };
        new JournalStore(instance, contracts).Create(JsonSerializer.SerializeToUtf8Bytes(document));
    }
}
