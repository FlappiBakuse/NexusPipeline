using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Extensibility.Secrets;

namespace NexusPipeline.Extensibility.Scheduling;

/// <summary>Exposes private task metadata without manufacturing a recurring schedule.</summary>
public interface IManualWorkHandler : IScheduledWorkHandler
{
    /// <summary>A fixed deterministic projection version; null explicitly leaves durable credential effects unavailable.</summary>
    string? IdentityVersion => null;

    /// <summary>Projects only authentication identity from actual protected material; Host ports are unavailable during this synchronous callback.</summary>
    SecretDraft ProjectIdentity(string field, SecretDraft actualStoredMaterial) => throw new NotSupportedException("work_identity_unavailable");

    /// <summary>Purely verifies allowed same-run credential rotation; unavailable implementations reject rotation continuity.</summary>
    bool CanRotateIdentity(string field, SecretDraft before, SecretDraft after) => false;

    /// <summary>Returns current metadata only for an existing task owned by this provider.</summary>
    ValueTask<ManualWorkTask?> ReadTaskAsync(Guid taskId, CancellationToken cancellationToken);

    /// <summary>Checks the frozen private revision before any task effect; the Host owns invocation identities and budget.</summary>
    ValueTask<ScheduledWorkResult> ExecuteManualAsync(ManualWorkInvocation invocation, CancellationToken cancellationToken);
}
