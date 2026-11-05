using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Scheduling;

[SupportedOSPlatform("windows")]
internal static class ScheduleQueuedActivities
{
    internal static void RegisterWaiting(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ResourceLeaseManager resources, ResourceOperationLease metadataRoot)
    {
        lease.VerifyHeld(instance);
        using var held = metadataRoot.Borrow(resources, metadataRoot.OperationId, [new("scheduling:state", ResourceClaimMode.Mutation)]);
        try { held.Use(() =>
        {
            using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
            using var transaction = connection.BeginTransaction(deferred: true);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT c.occurrence_id,0 FROM scheduled_occurrences c LEFT JOIN operations o ON o.operation_id=c.start_operation_id LEFT JOIN schedule_occurrence_outcomes b ON b.occurrence_id=c.occurrence_id WHERE o.operation_id IS NULL AND b.occurrence_id IS NULL UNION ALL SELECT c.occurrence_id,1 FROM startup_queue_occurrences c LEFT JOIN operations o ON o.operation_id=c.start_operation_id LEFT JOIN startup_queue_outcomes b ON b.occurrence_id=c.occurrence_id WHERE o.operation_id IS NULL AND b.occurrence_id IS NULL ORDER BY 1 LIMIT 8193;";
            var ids = new List<(Guid Id, bool Startup)>();
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    if (ids.Count == 8192) throw new IOException("schedule_pending_capacity");
                    ids.Add((StartupQueueRows.ParseId(reader.GetString(0)), reader.GetInt32(1) == 1));
                }
            foreach (var (id, startup) in ids)
            {
                using var origin = startup ? PreparedExecutionOrigin.CaptureStartup(instance, database, lease, id) :
                    PreparedExecutionOrigin.Capture(instance, database, lease, id);
                _ = origin.RegisterUnderRoot(resources, metadataRoot);
            }
            return true;
        }); }
        catch
        {
            resources.ReportUncertainOwnership(metadataRoot, "schedule_activity_reconciliation_required");
            throw;
        }
    }

}
