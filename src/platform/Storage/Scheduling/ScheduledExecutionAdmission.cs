using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Scheduling;

[SupportedOSPlatform("windows")]
internal sealed class ScheduledExecutionAdmission(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
    ExecutionAdmissionService execution, TimeProvider clock) : IScheduledExecutionAdmission
{
    public ValueTask<IExecutionAdmissionReceipt> StartAsync(ScheduledOccurrence occurrence, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var origin = PreparedExecutionOrigin.Capture(instance, database, lease, occurrence.Frozen.OccurrenceId, revalidate: true);
        origin.RequireOccurrence(occurrence);
        return ValueTask.FromResult<IExecutionAdmissionReceipt>(execution.StartScheduled(origin, clock.GetUtcNow()));
    }
}
