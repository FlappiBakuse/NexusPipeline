using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class ExecutionAdmissionDraft : IExecutionAdmissionDraft
{
    internal object Owner { get; }
    internal ExecutionPlanDraft Draft { get; }
    public IReadOnlyList<ResourceClaim> Claims { get; }
    internal ExecutionTimeBudget TimeBudget { get; }
    internal DateTimeOffset FirstPreparedAtUtc { get; }
    internal bool Automatic { get; }

    internal ExecutionAdmissionDraft(object owner, ExecutionPlanDraft draft, TimeProvider clock, long startedTimestamp,
        DateTimeOffset? firstPreparedAtUtc = null, ExecutionTimeBudget? sharedBudget = null, bool automatic = false)
    {
        Owner = owner; Draft = draft; FirstPreparedAtUtc = firstPreparedAtUtc ?? clock.GetUtcNow();
        Automatic = automatic;
        var plan = draft.ReadPlan() ?? throw new ExecutionSelectionException(draft.Explanation.ReasonCode);
        var wire = plan.ReadWire();
        Claims = ExecutionPlanResources.Claims(wire);
        TimeBudget = sharedBudget ?? new(clock, startedTimestamp, TimeSpan.FromSeconds(wire.Budget.TotalTimeoutSeconds),
            DateTimeOffset.Parse(wire.RetryPolicy.DeadlineUtc, System.Globalization.CultureInfo.InvariantCulture));
    }

    public void Dispose() => Draft.Dispose();
}
