namespace NexusPipeline.Core.Execution;

public enum RetrySafety { Unknown, Safe }
public enum WriterState { Unknown, Running, Stopped }
public enum AttemptOutcome { Interrupted, Failed, Succeeded, Cancelled, Blocked, Skipped, Partial, Unverified }

public sealed record FrozenRetryPolicy(
    RetrySafety Safety,
    bool HostApproved,
    int MaximumAttempts,
    DateTimeOffset DeadlineUtc,
    TimeSpan Backoff,
    string IdempotencyKey);

public sealed record RetryEvidence(
    AttemptOutcome PreviousOutcome,
    WriterState Writers,
    bool IsConfigurationRestored,
    bool HasQuarantinedResources,
    bool AreFrozenIdentitiesValid,
    int CompletedAttempts,
    DateTimeOffset PreviousEndedAt);

public sealed record PostDeferralDecision(bool ShouldDefer, bool RequiresConfigurationRestoration, string ReasonCode);

public sealed record RetryDecision(bool IsEligible, string ReasonCode, DateTimeOffset? NotBefore)
{
    public static AttemptOutcome Classify(AttemptFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!Enum.IsDefined(facts.Engine) || !Enum.IsDefined(facts.Business) || !Enum.IsDefined(facts.Lifecycle) ||
            !Enum.IsDefined(facts.Recovery) || facts.Engine == EngineOutcome.Running ||
            facts.Lifecycle is LifecycleOutcome.Running or LifecycleOutcome.NotStarted ||
            facts.Lifecycle == LifecycleOutcome.Completed && facts.Engine == EngineOutcome.NotStarted)
            throw new ArgumentException("retry_attempt_facts_not_terminal");
        return ClassifyDimensions(facts.Engine, facts.Business, facts.Lifecycle, facts.ReasonCode);
    }

    public static AttemptOutcome ClassifyCollectedPrimary(ExecutionStepFact actualCollected)
    {
        ArgumentNullException.ThrowIfNull(actualCollected);
        if (!Enum.IsDefined(actualCollected.Engine) || !Enum.IsDefined(actualCollected.Business) || actualCollected.Engine == EngineOutcome.Running)
            throw new ArgumentException("retry_collected_facts_invalid");
        return actualCollected.Engine == EngineOutcome.NotStarted ? AttemptOutcome.Unverified :
            ClassifyDimensions(actualCollected.Engine, actualCollected.Business, LifecycleOutcome.Completed, actualCollected.ReasonCode);
    }

    private static AttemptOutcome ClassifyDimensions(EngineOutcome engine, BusinessOutcome business, LifecycleOutcome lifecycle, string reason) => lifecycle switch
    {
        LifecycleOutcome.Interrupted => AttemptOutcome.Interrupted,
        LifecycleOutcome.Cancelled => AttemptOutcome.Cancelled,
        LifecycleOutcome.Blocked => AttemptOutcome.Blocked,
        LifecycleOutcome.Skipped => AttemptOutcome.Skipped,
        LifecycleOutcome.Completed when business == BusinessOutcome.Partial => AttemptOutcome.Partial,
        LifecycleOutcome.Completed when engine == EngineOutcome.Cancelled || business == BusinessOutcome.Cancelled => AttemptOutcome.Cancelled,
        LifecycleOutcome.Completed when engine == EngineOutcome.Failed && business == BusinessOutcome.NotApplicable &&
            reason == "execution_judgement_not_applicable" => AttemptOutcome.Succeeded,
        LifecycleOutcome.Completed when reason == "execution_business_succeeded" &&
            engine is EngineOutcome.Failed or EngineOutcome.Unknown && business is BusinessOutcome.Succeeded or BusinessOutcome.NotApplicable => AttemptOutcome.Succeeded,
        LifecycleOutcome.Completed when engine == EngineOutcome.Unknown && reason == "execution_success_grace_expired" &&
            business == BusinessOutcome.Succeeded => AttemptOutcome.Succeeded,
        LifecycleOutcome.Completed when engine == EngineOutcome.Unknown && reason is "execution_business_failed" or "execution_step_stall_timeout" => AttemptOutcome.Failed,
        LifecycleOutcome.Completed when engine == EngineOutcome.Failed || business == BusinessOutcome.Failed => AttemptOutcome.Failed,
        LifecycleOutcome.Completed when engine == EngineOutcome.Succeeded && business is BusinessOutcome.Succeeded or BusinessOutcome.NotApplicable => AttemptOutcome.Succeeded,
        _ => AttemptOutcome.Unverified
    };

    public static RetryDecision Evaluate(FrozenRetryPolicy policy, RetryEvidence evidence, DateTimeOffset now) =>
        EvaluateCore(policy, evidence, null, now);

    public static RetryDecision Evaluate(FrozenRetryPolicy policy, RetryEvidence evidence, ExecutionBudgetSnapshot budget, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(budget);
        return EvaluateCore(policy, evidence, budget, now);
    }

    public static PostDeferralDecision EvaluatePostDeferral(FrozenRetryPolicy policy, RetryEvidence actualCollected,
        ExecutionBudgetSnapshot budget, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(budget);
        var result = EvaluateCore(policy, actualCollected, budget, now, requireRestoration: false);
        var allowed = result.IsEligible || result.ReasonCode == "retry_backoff";
        return new(allowed, allowed && !actualCollected.IsConfigurationRestored,
            allowed ? actualCollected.IsConfigurationRestored ? "retry_post_deferred" : "retry_post_pending_restoration" : result.ReasonCode);
    }

    private static RetryDecision EvaluateCore(FrozenRetryPolicy policy, RetryEvidence evidence, ExecutionBudgetSnapshot? budget, DateTimeOffset now, bool requireRestoration = true)
    {
        ArgumentNullException.ThrowIfNull(policy); ArgumentNullException.ThrowIfNull(evidence);
        if (!Enum.IsDefined(policy.Safety) || !Enum.IsDefined(evidence.PreviousOutcome) || !Enum.IsDefined(evidence.Writers) ||
            now.Offset != TimeSpan.Zero || policy.DeadlineUtc.Offset != TimeSpan.Zero || evidence.PreviousEndedAt.Offset != TimeSpan.Zero ||
            evidence.PreviousEndedAt > now || policy.MaximumAttempts > 99 || policy.Backoff > TimeSpan.FromDays(1) ||
            policy.IdempotencyKey is null || policy.IdempotencyKey.Length > 512 || policy.IdempotencyKey.Any(char.IsControl))
            return Rejected("retry_evidence_invalid");
        if (policy.Safety != RetrySafety.Safe || !policy.HostApproved)
            return Rejected("retry_not_approved");
        if (evidence.PreviousOutcome is not (AttemptOutcome.Interrupted or AttemptOutcome.Failed))
            return Rejected("attempt_not_retryable");
        if (evidence.Writers != WriterState.Stopped || requireRestoration && !evidence.IsConfigurationRestored)
            return Rejected("recovery_not_confirmed");
        if (evidence.HasQuarantinedResources || !evidence.AreFrozenIdentitiesValid)
            return Rejected("frozen_resources_unavailable");
        if (policy.MaximumAttempts < 1 || evidence.CompletedAttempts < 1 || evidence.CompletedAttempts >= policy.MaximumAttempts)
            return Rejected("attempt_budget_exhausted");
        if (policy.DeadlineUtc <= now || policy.Backoff < TimeSpan.Zero || string.IsNullOrEmpty(policy.IdempotencyKey))
            return Rejected("retry_policy_invalid_or_expired");
        DateTimeOffset next;
        try { next = evidence.PreviousEndedAt + policy.Backoff; }
        catch (ArgumentOutOfRangeException) { return Rejected("retry_policy_invalid_or_expired"); }
        if (next >= policy.DeadlineUtc)
            return Rejected("retry_deadline_precedes_backoff");
        if (budget is not null)
        {
            var waiting = next > now ? next - now : TimeSpan.Zero;
            if (budget.ConsumedMilliseconds < 0 || budget.Remaining <= waiting || budget.ExhaustionReason is not null)
                return Rejected("retry_total_budget_exhausted");
        }
        return now < next ? new(false, "retry_backoff", next) : new(true, "retry_eligible", next);
    }

    private static RetryDecision Rejected(string reason) => new(false, reason, null);
}
