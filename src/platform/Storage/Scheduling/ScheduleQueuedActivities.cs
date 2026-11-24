using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Scheduling;
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
            using var connection = DatabaseConnections.Open(database.DatabasePath);
            using var transaction = DatabaseConnections.BeginRead(connection, DatabaseDomain.Runtime | DatabaseDomain.Automation | DatabaseDomain.System);
            var ids = ScheduleWaitingCapacity.ReadActivityCandidatesInside(connection, transaction);
            if (ids.Count == 0) { transaction.Commit(); return true; }
            var byMinute = ids.Where(item => item.Minute.HasValue).ToLookup(item => item.Minute.GetValueOrDefault(), item => item.Id);
            var verified = new Dictionary<Guid, ScheduleMinuteRows.PendingMinute>();
            foreach (var (id, minute) in ids)
            {
                if (minute is { } source)
                {
                    if (!verified.TryGetValue(source, out var saved))
                    {
                        saved = ScheduleMinuteRows.PendingMinute.Read(connection, transaction, source, instance.InstanceId, byMinute[source].ToHashSet());
                        verified.Add(source, saved);
                    }
                    PreparedExecutionOrigin.RegisterWaitingUnderRoot(instance, database, lease, resources, metadataRoot, saved, id);
                }
                else
                {
                    using var origin = PreparedExecutionOrigin.CaptureStartup(instance, database, lease, id);
                    _ = origin.RegisterUnderRoot(resources, metadataRoot);
                }
            }
            transaction.Commit();
            return true;
        }); }
        catch
        {
            resources.ReportUncertainOwnership(metadataRoot, "schedule_activity_reconciliation_required");
            throw;
        }
    }

}
