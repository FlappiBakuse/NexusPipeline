namespace NexusPipeline.Core.Scheduling;

public sealed class PublishedWorkEntry
{
    private readonly ScheduledWorkRegistry _owner;
    internal object RegistrationIdentity { get; }
    public string ExtensionId { get; }
    public string ProviderId { get; }
    public Guid ActivationId { get; }
    public bool SupportsManual => _owner.SupportsManual(this);

    internal PublishedWorkEntry(ScheduledWorkRegistry owner, object registration, string extension,
        string provider, Guid activation)
    { _owner = owner; RegistrationIdentity = registration; ExtensionId = extension; ProviderId = provider; ActivationId = activation; }

    public ValueTask<ObservedWorkPlans> ReadPlansAsync(CancellationToken cancellation = default) =>
        _owner.ReadPlansAsync(this, cancellation);

    public ValueTask<ObservedManualTask?> ReadManualAsync(Guid taskId, CancellationToken cancellation = default) =>
        _owner.ReadManualAsync(this, taskId, cancellation);

    public ValueTask<IDisposable> QuiesceAsync(IReadOnlyList<Guid> taskIds, CancellationToken cancellation = default) =>
        _owner.QuiesceAsync(this, taskIds, cancellation);

    public WorkTaskUse EnterTask(Guid taskId) => _owner.EnterTask(this, taskId);
    public void RequireExecution(Guid taskId, Guid runId) => _owner.RequireExecution(this, taskId, runId);
    public string RequireIdentity() => _owner.RequireIdentity(this);
    public Core.Secrets.SecretValue ProjectIdentity(string field, Core.Secrets.SecretValue material) => _owner.ProjectIdentity(this, field, material);
    public bool CanRotateIdentity(string field, Core.Secrets.SecretValue before, Core.Secrets.SecretValue after) => _owner.CanRotateIdentity(this, field, before, after);

    public T Use<T>(Func<T> admitted)
    {
        ArgumentNullException.ThrowIfNull(admitted);
        _owner.RequireCurrent(this);
        return admitted();
    }

    public ValueTask<ScheduledBusinessResult> DispatchPlannedAsync(PlannedScheduledInvocation invocation, CancellationToken cancellation = default) =>
        _owner.DispatchPlannedAsync(this, invocation, cancellation);

    public ValueTask<ScheduledBusinessResult> DispatchAsync(FrozenScheduledInvocation invocation, CancellationToken cancellation = default) =>
        _owner.DispatchEntryAsync(this, invocation, cancellation);

    public ValueTask<ScheduledBusinessResult> DispatchManualAsync(ManualInvocation invocation, CancellationToken cancellation = default) =>
        _owner.DispatchManualAsync(this, invocation, cancellation);
}

public sealed class ObservedWorkPlans
{
    public PublishedWorkEntry Entry { get; }
    public ScheduledWorkPlans Plans { get; }
    internal ObservedWorkPlans(PublishedWorkEntry entry, ScheduledWorkPlans plans)
    { Entry = entry; Plans = plans; }
}
