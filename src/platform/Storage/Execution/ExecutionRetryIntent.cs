using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record ExecutionRetryIntent(string Format, int Version, Guid InstanceId, string SubjectId,
    Guid PreviousOperationId, Guid PreviousJournalId, BlobReference PreviousPlan, BlobReference TotalPlan, PendingExecutionRetry Frozen);

internal static class RetryIntentCodec
{
    internal const string Kind = "execution.retry.prepare";
    internal static Guid Id(Guid run, int ordinal, string domain) => new(SHA256.HashData(Encoding.UTF8.GetBytes(
        "nexus.execution.retry/1/" + domain + "/" + run.ToString("D") + "/" + ordinal.ToString(CultureInfo.InvariantCulture))).AsSpan(0, 16), true);
    internal static OperationIdentity Identity(ExecutionRetryIntent value) => new(Id(value.Frozen.RunId, value.Frozen.NextOrdinal, "prepare"),
        value.SubjectId, Convert.ToHexStringLower(SHA256.HashData(Encode(value))), Kind);
    internal static byte[] Encode(ExecutionRetryIntent value) => BoundedJson.Serialize(value, StepResultCodec.Options);

    internal static ExecutionRetryIntent Decode(ReadOnlyMemory<byte> bytes)
    {
        using var json = StrictJson.Parse(bytes);
        var value = json.RootElement.Deserialize<ExecutionRetryIntent>(StepResultCodec.Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(json.RootElement, JsonSerializer.SerializeToElement(value, StepResultCodec.Options)) ||
            value.Format != "nexus.execution.retry" || value.Version != 1) throw Invalid();
        return Require(value);
    }

    private static ExecutionRetryIntent Require(ExecutionRetryIntent value)
    {
        var f = value.Frozen ?? throw Invalid();
        if (value.InstanceId == Guid.Empty || string.IsNullOrWhiteSpace(value.SubjectId) || value.SubjectId.Length > 256 ||
            value.PreviousOperationId == Guid.Empty || value.PreviousJournalId == Guid.Empty ||
            f.RunId == Guid.Empty || f.PreviousAttemptId == Guid.Empty || f.NextOrdinal is < 2 or > 99 ||
            f.NextOperationId != Id(f.RunId, f.NextOrdinal, "start") || f.NextAttemptId != Id(f.RunId, f.NextOrdinal, "attempt") ||
            f.NextJournalId != Id(f.RunId, f.NextOrdinal, "journal") || f.IdempotencyKey != f.RunId.ToString("D") ||
            f.NotBefore.Offset != TimeSpan.Zero || f.DeadlineUtc.Offset != TimeSpan.Zero || f.NotBefore >= f.DeadlineUtc ||
            f.ConsumedMilliseconds < 0 || !NexusPipeline.Core.Configuration.FileTreePolicy.Digest(f.PreviousReceiptDigest) ||
            f.FrozenPlanDigest != value.TotalPlan.Sha256) throw Invalid();
        StepEvidenceCodec.Plan(value.PreviousPlan); StepEvidenceCodec.Plan(value.TotalPlan);
        return value;
    }

    internal static FrozenRetryPolicy Policy(NexusPipeline.Platform.Serialization.Protocol.ExecutionPlan plan) =>
        new(plan.RetryPolicy.Safety == "safe" ? RetrySafety.Safe : RetrySafety.Unknown, plan.RetryPolicy.HostApproved,
            checked((int)plan.RetryPolicy.MaximumAttempts), DateTimeOffset.Parse(plan.RetryPolicy.DeadlineUtc, CultureInfo.InvariantCulture),
            TimeSpan.FromSeconds(plan.RetryPolicy.BackoffSeconds), plan.RetryPolicy.IdempotencyKey);

    internal static ExecutionRetryIntent? Candidate(RuntimeInstance instance, AcceptedExecutionAdmission accepted,
        AttemptWriterState state, JournalDocument terminal, RunBudgetObservation measured)
    {
        var facts = AttemptResultCodec.Read(terminal.Root, state.Attempt)?.Facts ?? throw Invalid();
        if (RetryDecision.Classify(facts) != AttemptOutcome.Interrupted ||
            !ExecutionInvocationHistory.CanResume(state.Total, terminal.Root, state.PreviousInvocations)) return null;
        var policy = Policy(state.Total);
        var snapshot = new ExecutionBudgetSnapshot(measured.ConsumedMilliseconds,
            measured.TotalMilliseconds == 0 ? TimeSpan.MaxValue : TimeSpan.FromMilliseconds(measured.TotalMilliseconds - measured.ConsumedMilliseconds), measured.ClockRegressed ? "clock_unknown" : null);
        var decision = RetryDecision.Evaluate(policy, new(RetryDecision.Classify(facts), WriterState.Stopped,
            facts.Recovery is RecoveryOutcome.Restored or RecoveryOutcome.NotRequired, false, true, facts.Ordinal, measured.ObservedAtUtc),
            snapshot, measured.ObservedAtUtc);
        if (!decision.IsEligible && decision.ReasonCode != "retry_backoff") return null;
        if (state.Total.Steps.Any(step => step.Provider?.Retry is null ||
            step.Target is not null || step.JudgementWorker is not null || step.Provider.Launcher is not null))
            throw Invalid();
        if (!RetryHookEvidence.CanRetry(terminal.Root, state.Total)) return null;
        var next = facts.Ordinal + 1;
        return new("nexus.execution.retry", 1, instance.InstanceId, accepted.Identity.SubjectId, accepted.Identity.OperationId,
            state.Journal.JournalId, accepted.Prepared.Attempt.Reference, accepted.Prepared.Total.Reference,
            new(facts.RunId, facts.AttemptId, Id(facts.RunId, next, "attempt"), Id(facts.RunId, next, "journal"),
                Id(facts.RunId, next, "start"), next, policy.IdempotencyKey, decision.NotBefore ?? throw Invalid(),
                policy.DeadlineUtc, measured.ConsumedMilliseconds, accepted.Prepared.Total.Reference.Sha256,
                measured.TerminalReceiptDigest ?? throw Invalid()));
    }

    internal static IOException Invalid() => new("execution_retry_source_unbound");
}
