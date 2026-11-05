namespace NexusPipeline.Extensibility.Scheduling;

/// <summary>Durable claims use the actual callback, Host calendar and protected stored credential identity.</summary>
public interface IWorkEffects
{
    /// <summary>Reserves the entire activity before any external protocol, including token refresh.</summary>
    ValueTask<WorkEffectLease> BeginAsync(string field, long expectedSecretRevision, string activityId, CancellationToken cancellationToken);
    /// <summary>Role and server claims remain subordinate to the reserved activity.</summary>
    ValueTask<WorkEffectLease> AcquireAsync(Guid activityLeaseId, string roleId, string serverId, CancellationToken cancellationToken);
    /// <summary>Must commit before a request capable of changing external state.</summary>
    ValueTask SubmittedAsync(Guid leaseId, CancellationToken cancellationToken);
    /// <summary>Positive protocol evidence can resolve an unknown claim without granting another submission.</summary>
    ValueTask ConfirmedAsync(Guid leaseId, string evidenceCode, CancellationToken cancellationToken);
    /// <summary>Completes the activity only after its recorded roles have all been confirmed.</summary>
    ValueTask CompleteAsync(Guid activityLeaseId, CancellationToken cancellationToken);
}

/// <summary>Only New grants submission; Busy and Unknown never authorize an external write.</summary>
public enum WorkEffectState
{
    /// <summary>The actual callback owns a new claim and may reserve a submission.</summary>
    New,
    /// <summary>Another callback still owns the claim.</summary>
    Busy,
    /// <summary>The effect needs positive read-only confirmation and cannot be submitted again.</summary>
    Unknown,
    /// <summary>Positive business evidence was durably recorded.</summary>
    Confirmed
}

/// <summary>The opaque lease belongs to the actual callback and cannot be reused by a later invocation.</summary>
public sealed record WorkEffectLease(Guid Id, WorkEffectState State);
