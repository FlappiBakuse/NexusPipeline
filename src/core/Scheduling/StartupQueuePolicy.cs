using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Core.Automation;

namespace NexusPipeline.Core.Scheduling;

public sealed record StartupQueueOccurrence(Guid OccurrenceId, Guid RunId, Guid StartOperationId, Guid QueueId,
    long QueueRevision, long CatalogRevision, long SettingsRevision);
public sealed record StartupQueueBatch(Guid InstanceId, Guid Generation, DateTimeOffset CreatedAtUtc,
    ImmutableArray<StartupQueueOccurrence> Occurrences);

public static class StartupQueuePolicy
{
    public static StartupQueueBatch Freeze(Guid instanceId, Guid generation, AutomationCatalog catalog,
        long settingsRevision, DateTimeOffset now)
    {
        AutomationCatalogPolicy.Validate(catalog);
        if (instanceId == Guid.Empty || generation == Guid.Empty || settingsRevision < 0 ||
            now.Offset != TimeSpan.Zero || now <= DateTimeOffset.UnixEpoch)
            throw new ArgumentException("startup_schedule_identity_invalid");
        var occurrences = catalog.Queues.Where(queue => queue.RunOnStartup && !queue.ScriptIds.IsEmpty)
            .OrderBy(queue => queue.Id).Select(queue => CreateOccurrence(instanceId, generation, queue.Id,
                queue.Revision, catalog.Revision, settingsRevision)).ToImmutableArray();
        return new(instanceId, generation, now, occurrences);
    }

    public static StartupQueueOccurrence CreateOccurrence(Guid instanceId, Guid generation, Guid queueId,
        long queueRevision, long catalogRevision, long settingsRevision)
    {
        if (instanceId == Guid.Empty || generation == Guid.Empty || queueId == Guid.Empty ||
            queueRevision is < 1 or > 9007199254740991 || catalogRevision is < 1 or > 9007199254740991 ||
            settingsRevision is < 0 or > 9007199254740991) throw new ArgumentException("startup_schedule_identity_invalid");
        return new(Id(instanceId, generation, queueId, "occurrence"), Id(instanceId, generation, queueId, "run"),
            Id(instanceId, generation, queueId, "start"), queueId, queueRevision, catalogRevision, settingsRevision);
    }

    private static Guid Id(Guid instance, Guid generation, Guid queue, string domain) => new(
        SHA256.HashData(Encoding.UTF8.GetBytes($"nexus.schedule.startup/1/{domain}/{instance:D}/{generation:D}/{queue:D}")).AsSpan(0, 16), bigEndian: true);
}
