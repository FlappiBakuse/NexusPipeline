using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Scheduling;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class StartupQueueStore(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
    ResourceLeaseManager resources, TimeProvider clock)
{
    internal StartupQueueBatch Commit()
    {
        RequireInstance();
        using var root = resources.TryAcquire(lease.StartupGeneration,
            [new("scheduling:state", ResourceClaimMode.Mutation), new("automation:definitions", ResourceClaimMode.Reference),
                new("settings:host", ResourceClaimMode.Reference)]).Lease ?? throw new IOException("schedule_admission_blocked");
        return CommitUnderRoot(root);
    }

    internal StartupQueueBatch CommitUnderRoot(ResourceOperationLease root)
    {
        RequireInstance();
        using var held = root.Borrow(resources, root.OperationId,
            [new("scheduling:state", ResourceClaimMode.Mutation), new("automation:definitions", ResourceClaimMode.Reference),
                new("settings:host", ResourceClaimMode.Reference)]);
        return held.Use(() => CommitOwned(root));
    }

    private StartupQueueBatch CommitOwned(ResourceOperationLease root)
    {
        var committed = lease.PublishExecution(() => database.Write((connection, transaction) =>
        {
            var prior = StartupQueueRows.ReadBatch(connection, transaction, lease.StartupGeneration, instance.InstanceId);
            if (prior is not null) return prior;
            var catalog = AutomationRows.Read(connection, transaction);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT revision FROM system.settings_current WHERE singleton=1;";
            var settings = command.ExecuteScalar() is long revision ? revision : throw StartupQueueRows.Invalid();
            var batch = StartupQueuePolicy.Freeze(instance.InstanceId, lease.StartupGeneration, catalog, settings, clock.GetUtcNow());
            StartupQueueRows.Insert(connection, transaction, batch);
            return StartupQueueRows.ReadBatch(connection, transaction, lease.StartupGeneration, instance.InstanceId) ?? throw StartupQueueRows.Invalid();
        }));
        lease.PublishExecution(() => ScheduleQueuedActivities.RegisterWaiting(instance, database, lease, resources, root));
        return committed;
    }

    internal IReadOnlyList<SavedStartupQueue> ReadWaiting(int limit = 64)
    {
        RequireInstance();
        if (limit is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT c.occurrence_id FROM main.startup_queue_occurrences c LEFT JOIN main.operations o ON o.operation_id=c.start_operation_id LEFT JOIN main.startup_queue_outcomes b ON b.occurrence_id=c.occurrence_id WHERE c.generation=$generation AND o.operation_id IS NULL AND b.occurrence_id IS NULL ORDER BY c.queue_id LIMIT $limit;";
        command.Parameters.AddWithValue("$generation", lease.StartupGeneration.ToString("D")); command.Parameters.AddWithValue("$limit", limit);
        var ids = new List<Guid>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) ids.Add(StartupQueueRows.ParseId(reader.GetString(0)));
        if (ids.Count == 0) return [];
        var batch = StartupQueueRows.ReadBatch(connection, transaction, lease.StartupGeneration, instance.InstanceId) ?? throw StartupQueueRows.Invalid();
        return ids.Select(id => StartupQueueRows.Read(connection, transaction, id, instance.InstanceId, batch)).ToArray();
    }

    internal void Block(SavedStartupQueue expected, string reason)
    {
        RequireInstance(); StartupQueueRows.RequireReason(reason);
        if (expected.Batch.InstanceId != instance.InstanceId || expected.Batch.Generation != lease.StartupGeneration) throw StartupQueueRows.Invalid();
        using var origin = PreparedExecutionOrigin.CaptureStartup(instance, database, lease, expected.Occurrence.OccurrenceId, allowBlocked: true);
        using var root = origin.AcquireMetadata(resources);
        lease.PublishExecution(() =>
        {
            var blocked = database.Write((connection, transaction) =>
            {
                var actual = StartupQueueRows.Read(connection, transaction, expected.Occurrence.OccurrenceId, instance.InstanceId);
                if (StartupQueueRows.Encode(actual.Batch) != StartupQueueRows.Encode(expected.Batch) || actual.Occurrence != expected.Occurrence)
                    throw StartupQueueRows.Invalid();
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "INSERT INTO main.startup_queue_outcomes(occurrence_id,reason_code) SELECT $id,$reason WHERE NOT EXISTS(SELECT 1 FROM main.operations WHERE operation_id=$start) ON CONFLICT(occurrence_id) DO NOTHING;";
                command.Parameters.AddWithValue("$id", actual.Occurrence.OccurrenceId.ToString("D"));
                command.Parameters.AddWithValue("$reason", reason); command.Parameters.AddWithValue("$start", actual.Occurrence.StartOperationId.ToString("D"));
                command.ExecuteNonQuery();
                return StartupQueueRows.Read(connection, transaction, expected.Occurrence.OccurrenceId, instance.InstanceId).BlockReason is not null;
            });
            if (blocked) { lease.RunBudgets.ReleaseBlockedStartup(database, expected.Occurrence.OccurrenceId); origin.ReleaseBlocked(resources); }
        });
    }

    internal void RetirePriorOwners()
    {
        RequireInstance();
        lease.PublishExecution(() => database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO main.startup_queue_outcomes(occurrence_id,reason_code) SELECT c.occurrence_id,'startup_owner_restarted' FROM main.startup_queue_occurrences c LEFT JOIN main.operations o ON o.operation_id=c.start_operation_id WHERE c.generation<>$generation AND o.operation_id IS NULL ON CONFLICT(occurrence_id) DO NOTHING;";
            command.Parameters.AddWithValue("$generation", lease.StartupGeneration.ToString("D")); command.ExecuteNonQuery(); return true;
        }));
    }

    internal bool HasPending()
    {
        RequireInstance();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT NOT EXISTS(SELECT 1 FROM main.startup_queue_batches WHERE generation=$generation) OR EXISTS(SELECT 1 FROM main.startup_queue_occurrences c LEFT JOIN main.operations o ON o.operation_id=c.start_operation_id LEFT JOIN main.startup_queue_outcomes b ON b.occurrence_id=c.occurrence_id WHERE c.generation=$generation AND o.operation_id IS NULL AND b.occurrence_id IS NULL);";
        command.Parameters.AddWithValue("$generation", lease.StartupGeneration.ToString("D"));
        return (long)command.ExecuteScalar()! != 0;
    }

    private void RequireInstance()
    {
        lease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot ||
            !StringComparer.OrdinalIgnoreCase.Equals(database.DatabasePath, Path.Combine(instance.RuntimeRoot, "state", "runtime.db")))
            throw StartupQueueRows.Invalid();
    }
}
