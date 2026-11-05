namespace NexusPipeline.Core.Execution;

public enum ExecutionJudgementMode { None, Keywords, Script }

public sealed record JudgementInputIdentity(Guid AttemptId, Guid OwnerId);
public sealed record JudgementInvocation(Guid AttemptId, long Generation, bool IsFinal, Guid OwnerId);
public sealed record BusinessJudgementResult(BusinessOutcome Outcome, string Reason);
public sealed record BusinessJudgementDecision(BusinessOutcome Outcome, string ReasonCode, string Reason);
