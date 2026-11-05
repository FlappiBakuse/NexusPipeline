using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Scheduling;

namespace NexusPipeline.Platform.Storage.Extensions;

internal interface IInstalledWork
{
    AcceptedExecutionRun Activity { get; }
    Guid OperationId { get; }
    FrozenScheduledInvocation FrozenInvocation { get; }
    Guid TaskId { get; }
    string ProviderId { get; }
    string RequireIdentity();
    Core.Secrets.SecretValue ProjectIdentity(string field, Core.Secrets.SecretValue material);
    bool CanRotateIdentity(string field, Core.Secrets.SecretValue before, Core.Secrets.SecretValue after);
    long TaskRevision { get; }
    DateOnly LocalDate { get; }
    string TimeZoneId { get; }
    void RequireCurrent();
}
