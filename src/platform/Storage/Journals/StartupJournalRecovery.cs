using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Automation;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace NexusPipeline.Platform.Storage.Journals;

public sealed class StartupJournalRecovery(RuntimeInstance instance, ContractReader contracts, StateDatabase database, PayloadApplyRoots roots,
    Maintenance.UpdateReleaseTrust? maintenanceTrust = null)
{
    private readonly JournalRecoveryOwnership _ownership = new(database);

    public RecoveryIndex Recover(InstanceLease lease)
    {
        using var profile = StartupProfile.Begin("operations.recovery");
        lease.VerifyHeld(instance);
        using var reads = DatabaseReadScope.Hold(database.DatabasePath);
        InspectCompletedHistory();
        var registry = new RecoveryJournalRegistry(database);
        var before = registry.ReadActiveSnapshot();
        Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { kind = "startup.active-journals", count = before.Journals.Count }));
        InspectBindings(before);
        ReleaseStoppedMaintenance(lease, before);
        var evidence = new JournalRecoveryEvidence(instance, new(instance, contracts), database);
        IRecoveryEvidence completionEvidence = OperatingSystem.IsWindows()
            ? new SnapshotCompletionEvidence(instance, database, contracts) : evidence;
        var index = new RecoveryIndex();
        using (StartupProfile.Measure("recovery.publish"))
            new RecoveryCoordinator(registry, completionEvidence, index, _ownership).RecoverAndPublish(
                completionEvidence as IRecoveryPreflight ?? evidence, Handlers(lease));
        using (StartupProfile.Measure("recovery.configuration"))
            Configuration.ConfigurationEditRecovery.ReconcileAndPublish(instance, database, contracts, lease, index, roots);
        return index;
    }

    internal void VerifyReadOnly(RecoveryRegistrySnapshot snapshot)
    {
        InspectBindings(snapshot);
        _ownership.VerifyStopped(snapshot.Journals);
    }

    internal void InspectBindings(RecoveryRegistrySnapshot snapshot, bool completeActiveInventory = true)
    {
        using var timing = StartupProfile.Measure("history.bindings");
        using var parents = JournalReadScope.HoldParents(instance, contracts);
        if (snapshot.Journals.Count == 1 && Directory.Exists(Path.Combine(instance.RuntimeRoot, "journals", snapshot.Journals[0].JournalId.ToString("D"))))
            JournalReadScope.SelectParent(instance, contracts, snapshot.Journals[0].JournalId);
        RecoveryRegistryPolicy.Verify(snapshot);
        if (!completeActiveInventory)
        {
            new JournalRecoveryEvidence(instance, new(instance, contracts), database).VerifyRecognizedScope(snapshot.Journals);
            if (OperatingSystem.IsWindows()) new Configuration.SnapshotCompletionPreflight(instance, database, contracts).Verify(snapshot.Journals);
        }
        else if (OperatingSystem.IsWindows()) new SnapshotCompletionEvidence(instance, database, contracts).VerifyRecognizedAll(snapshot.Journals);
        else new JournalRecoveryEvidence(instance, new(instance, contracts), database).VerifyRecognizedAll(snapshot.Journals);
        var handlers = Handlers().ToDictionary(item => item.Kind);
        var prepared = new List<IJournalRecoveryWork>();
        try
        {
            foreach (var journal in snapshot.Journals)
            {
                using var preparation = StartupProfile.Measure("history.prepare." + journal.Kind);
                prepared.Add(handlers[journal.Kind].Prepare(journal));
            }
        }
        finally
        {
            for (var index = prepared.Count - 1; index >= 0; index--) prepared[index].Dispose();
        }
    }

    internal void InspectCompletedHistory()
    {
        using var timing = StartupProfile.Measure("history.completed");
        var registry = new RecoveryJournalRegistry(database);
        long count = 0;
        ExceptionDispatchInfo? failure = null;
        Parallel.ForEach(Partitioner.Create(registry.ReadCompleted(), EnumerablePartitionerOptions.NoBuffering),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8) },
            () => new HistoryReadScope(instance, database, contracts), (snapshot, loop, scope) =>
            {
                try
                {
                    InspectBindings(snapshot, completeActiveInventory: false);
                    Interlocked.Add(ref count, snapshot.Journals.Count);
                }
                catch (Exception error)
                {
                    Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(error), null);
                    loop.Stop();
                }
                return scope;
            }, scope => scope.Dispose());
        failure?.Throw();
        Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { kind = "startup.completed-journals", count }));
    }

    private void ReleaseStoppedMaintenance(InstanceLease lease, RecoveryRegistrySnapshot verified)
    {
        foreach (var journal in verified.Journals.Where(item => item.Kind == JournalKind.UpdateApply && item.Phase == JournalPhase.Committed))
        {
            var writers = _ownership.Observe(journal.JournalId);
            if (!writers.IsComplete || writers.State != NexusPipeline.Core.Execution.WriterState.Stopped) continue;
            lease.PublishExecution(() => database.Write((connection, transaction) =>
            {
                var current = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, journal.JournalId);
                if (current.OperationId != journal.OperationId || current.Phase != journal.Phase || current.Revision != journal.Revision ||
                    current.PlanDigest != journal.PlanDigest) throw RecoveryCapacityStore.Invalid();
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "UPDATE recovery_capacity_reservations SET remaining_journals=0,remaining_effects=0,remaining_jobs=0,released=1 " +
                    "WHERE owner_journal_id=$journal AND released=0 AND EXISTS(SELECT 1 FROM maintenance_operations " +
                    "WHERE operation_id=$operation AND state=2) AND NOT EXISTS(SELECT 1 FROM recovery_capacity_members m " +
                    "JOIN operation_journals j USING(journal_id) WHERE m.owner_journal_id=$journal AND j.phase<>'committed');";
                command.Parameters.AddWithValue("$journal", journal.JournalId.ToString("D"));
                command.Parameters.AddWithValue("$operation", journal.OperationId.ToString("D"));
                return command.ExecuteNonQuery();
            }));
        }
    }

    private IReadOnlyList<IJournalRecoveryHandler> Handlers(InstanceLease? executionLease = null) => [
        new ConfigurationRecoveryHandler(instance, contracts, database, JournalKind.ConfigExchange, id =>
        {
            var observed = _ownership.Observe(id);
            return new(instance.InstanceId, id, observed.IsComplete, observed.State);
        }, executionLease),
        new ConfigurationRecoveryHandler(instance, contracts, database, JournalKind.ConfigEdit, id =>
        {
            var observed = _ownership.Observe(id);
            return new(instance.InstanceId, id, observed.IsComplete, observed.State);
        }, executionLease),
        new CleanupRecoveryHandler(instance, contracts, database, id =>
        {
            var observed = _ownership.Observe(id);
            return new(instance.InstanceId, id, observed.IsComplete, observed.State);
        }, payloadRoots: roots, executionLease: executionLease, recoveryOwnership: _ownership),
        new PreparedRecoveryHandler(instance, contracts, database, id =>
        {
            var observed = _ownership.Observe(id);
            return new(instance.InstanceId, id, observed.IsComplete, observed.State);
        }, executionLease: executionLease),
        Payload(JournalKind.UpdateApply, executionLease), Payload(JournalKind.ExtensionApply, executionLease),
        Secret(),
        new AssetRecoveryHandler(instance, contracts, database, _ownership.Observe)
    ];

    private IJournalRecoveryHandler Secret()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return new Secrets.PreparationRecoveryHandler(instance, contracts, database, _ownership.Observe);
    }

    private PreparedPayloadHandler Payload(JournalKind kind, InstanceLease? executionLease) => new(instance, contracts, database, roots, kind, id =>
    {
        var observed = _ownership.Observe(id);
        return new(instance.InstanceId, id, observed.IsComplete, observed.State);
    }, recoveryOwnership: _ownership, executionLease: executionLease, maintenanceTrust: maintenanceTrust);
}
