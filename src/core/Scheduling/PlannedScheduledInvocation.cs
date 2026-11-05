namespace NexusPipeline.Core.Scheduling;

public sealed record PlannedScheduledInvocation
{
    public FrozenScheduledInvocation Invocation { get; }
    public long PlansRevision { get; }
    public long TaskRevision { get; }
    public Guid? OwnerTaskId { get; }

    public PlannedScheduledInvocation(FrozenScheduledInvocation invocation, long plansRevision, long taskRevision, Guid? ownerTaskId = null)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (plansRevision is < 1 or > 9007199254740991 || taskRevision is < 1 or > 9007199254740991 || taskRevision > plansRevision || ownerTaskId == Guid.Empty)
            throw new ArgumentException("scheduled_planned_revision_invalid");
        Invocation = invocation; PlansRevision = plansRevision; TaskRevision = taskRevision; OwnerTaskId = ownerTaskId;
    }
}

public interface IPlannedInvocationAuthorization : IScheduledInvocationAuthorization
{
    void AssertAllowed(PlannedScheduledInvocation invocation, string extensionId, string providerId);
}
