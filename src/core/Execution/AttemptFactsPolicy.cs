namespace NexusPipeline.Core.Execution;

public sealed record ExecutionStepFact(string StepId, EngineOutcome Engine, BusinessOutcome Business, string ReasonCode);

public static class AttemptFactsPolicy
{
    public static AttemptFacts Aggregate(Guid runId, Guid attemptId, int ordinal, IReadOnlyList<ExecutionStepFact> ordered,
        LifecycleOutcome lifecycle, RecoveryOutcome recovery, string reason)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        var steps = ordered.ToArray();
        if (runId == Guid.Empty || attemptId == Guid.Empty || ordinal is < 1 or > 99 || steps.Length is < 1 or > 4096 ||
            lifecycle is not (LifecycleOutcome.Completed or LifecycleOutcome.Cancelled or LifecycleOutcome.Interrupted or LifecycleOutcome.Blocked or LifecycleOutcome.Skipped) ||
            recovery is not (RecoveryOutcome.NotRequired or RecoveryOutcome.Restored or RecoveryOutcome.Quarantined) || !Reason(reason) ||
            steps.Any(step => step is null || string.IsNullOrWhiteSpace(step.StepId) || step.StepId.Length > 120 || step.StepId.Any(char.IsControl) ||
                !Enum.IsDefined(step.Engine) || step.Engine == EngineOutcome.Running || !Enum.IsDefined(step.Business) || !Reason(step.ReasonCode) ||
                step.Engine == EngineOutcome.NotStarted && step.Business is BusinessOutcome.Succeeded or BusinessOutcome.Partial or BusinessOutcome.Failed) ||
            steps.Select(step => step.StepId).Distinct(StringComparer.Ordinal).Count() != steps.Length)
            throw new ArgumentException("execution_terminal_facts_invalid");
        var engine = AggregateEngine(steps.Select(step => step.Engine).ToArray());
        if (engine == EngineOutcome.NotStarted && lifecycle == LifecycleOutcome.Completed ||
            lifecycle is LifecycleOutcome.Blocked or LifecycleOutcome.Skipped && engine != EngineOutcome.NotStarted)
            throw new ArgumentException("execution_terminal_lifecycle_unbound");
        if (lifecycle == LifecycleOutcome.Completed && engine != EngineOutcome.Succeeded)
        {
            var outcomes = steps.Select(RetryDecision.ClassifyCollectedPrimary).ToArray();
            if (outcomes.All(value => value == AttemptOutcome.Succeeded)) reason = "execution_business_succeeded";
            else if (engine == EngineOutcome.Unknown && outcomes.Contains(AttemptOutcome.Failed) &&
                outcomes.All(value => value is AttemptOutcome.Succeeded or AttemptOutcome.Failed)) reason = "execution_business_failed";
        }
        return new(runId, attemptId, ordinal, engine, Business(steps), lifecycle, recovery, reason);
    }

    public static EngineOutcome AggregateEngine(IReadOnlyList<EngineOutcome> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var values = observations.ToArray();
        if (values.Length is < 1 or > 4096 || values.Any(value => !Enum.IsDefined(value) || value == EngineOutcome.Running))
            throw new ArgumentException("execution_terminal_engines_invalid");
        foreach (var outcome in new[] { EngineOutcome.Unknown, EngineOutcome.Failed, EngineOutcome.Cancelled, EngineOutcome.Succeeded })
            if (values.Contains(outcome)) return outcome;
        return EngineOutcome.NotStarted;
    }

    private static BusinessOutcome Business(ExecutionStepFact[] steps)
    {
        var applicable = steps.Where(step => step.Business != BusinessOutcome.NotApplicable).Select(step => step.Business).ToArray();
        if (applicable.Length == 0) return BusinessOutcome.NotApplicable;
        if (applicable.Contains(BusinessOutcome.Unverified)) return BusinessOutcome.Unverified;
        if (applicable.Contains(BusinessOutcome.Cancelled))
            return applicable.All(outcome => outcome == BusinessOutcome.Cancelled) ? BusinessOutcome.Cancelled : BusinessOutcome.Unverified;
        if (applicable.Contains(BusinessOutcome.Partial) || applicable.Contains(BusinessOutcome.Succeeded) && applicable.Contains(BusinessOutcome.Failed))
            return BusinessOutcome.Partial;
        return applicable.Contains(BusinessOutcome.Failed) ? BusinessOutcome.Failed : BusinessOutcome.Succeeded;
    }

    private static bool Reason(string? value) => value is { Length: >= 1 and <= 80 } &&
        value[0] is >= 'a' and <= 'z' && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}
