using NexusPipeline.Core.Automation;

namespace NexusPipeline.Core.Execution;

public enum StepStopCause { Continue, BusinessFailed, SuccessGraceExpired, StallTimeout, StepTotalTimeout }

public static class StepStopPolicy
{
    public static readonly TimeSpan SuccessGrace = TimeSpan.FromSeconds(60);

    public static StepStopCause Evaluate(AutomationLimits limits, TimeSpan elapsed, TimeSpan idle,
        BusinessOutcome? persistedBusiness, TimeSpan? sinceAcceptedBusiness, bool observeStall)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.TotalTimeoutSeconds is < 0 or > 604800 || limits.StallTimeoutSeconds is < 0 or > 86400 ||
            elapsed < TimeSpan.Zero || idle < TimeSpan.Zero || idle > elapsed ||
            persistedBusiness is not (null or BusinessOutcome.Succeeded or BusinessOutcome.Failed or BusinessOutcome.Partial) ||
            persistedBusiness.HasValue != sinceAcceptedBusiness.HasValue ||
            sinceAcceptedBusiness is { } since && (since < TimeSpan.Zero || since > elapsed))
            throw new ArgumentException("execution_step_timing_invalid");
        if (limits.TotalTimeoutSeconds > 0 && elapsed >= TimeSpan.FromSeconds(limits.TotalTimeoutSeconds)) return StepStopCause.StepTotalTimeout;
        if (persistedBusiness == BusinessOutcome.Failed) return StepStopCause.BusinessFailed;
        if (persistedBusiness is BusinessOutcome.Succeeded or BusinessOutcome.Partial)
            return sinceAcceptedBusiness!.Value >= SuccessGrace ? StepStopCause.SuccessGraceExpired : StepStopCause.Continue;
        return observeStall && limits.StallTimeoutSeconds > 0 && idle >= TimeSpan.FromSeconds(limits.StallTimeoutSeconds)
            ? StepStopCause.StallTimeout : StepStopCause.Continue;
    }
}
