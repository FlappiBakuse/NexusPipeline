using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class AdmissionPublicationEvidence(RuntimeInstance instance, StateDatabase database,
    ContractReader contracts, PayloadApplyRoots roots, Guid? completedCleanup = null) : IRecoveryEvidence
{
    public void VerifyAll(IReadOnlyList<RegisteredJournal> registered)
    {
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot)
            throw new IOException("execution_admission_publication_instance_unbound");
        var registry = new RecoveryJournalRegistry(database);
        var snapshot = registry.ReadActiveSnapshot();
        RecoveryRegistryPolicy.Verify(snapshot);
        RequireRegistered(registered, snapshot.Journals);
        new StartupJournalRecovery(instance, contracts, database, roots).InspectBindings(snapshot, completeActiveInventory: false);
        JournalResourceClaims.VerifyScope(instance, database, contracts, snapshot.Journals, roots);
        var after = registry.ReadActiveSnapshot();
        RecoveryRegistryPolicy.Verify(after);
        RequireRegistered(snapshot.Journals, after.Journals);
        if (snapshot.Revision != after.Revision || snapshot.IsReady != after.IsReady ||
            !snapshot.Isolations.ToHashSet().SetEquals(after.Isolations) || !snapshot.FileEffects.ToHashSet().SetEquals(after.FileEffects))
            throw new IOException("execution_admission_publication_registry_changed");
        if (completedCleanup is { } id)
        {
            var completed = registry.ReadJournal(id);
            if (completed.Kind != JournalKind.FileCleanup) throw new IOException("cleanup_publication_kind_unbound");
            if (completed.Phase == JournalPhase.Committed)
                _ = CleanupJournalBinding.ReadExact(instance, contracts, completed,
                    new FileEffectOutbox(database).Read(id), database, roots);
        }
    }

    private static void RequireRegistered(IReadOnlyList<RegisteredJournal> expected, IReadOnlyList<RegisteredJournal> actual)
    {
        var byId = actual.ToDictionary(item => item.JournalId);
        if (expected.Count != actual.Count || expected.Select(item => item.JournalId).Distinct().Count() != expected.Count || expected.Any(item =>
            !byId.TryGetValue(item.JournalId, out var current) || current.OperationId != item.OperationId || current.Kind != item.Kind ||
            current.Phase != item.Phase || current.Revision != item.Revision || current.PlanPath != item.PlanPath || current.PlanDigest != item.PlanDigest ||
            !current.ResourceKeys.SequenceEqual(item.ResourceKeys)))
            throw new IOException("execution_admission_publication_registry_changed");
    }
}
