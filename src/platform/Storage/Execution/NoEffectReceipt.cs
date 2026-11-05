using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class NoEffectReceipt
{
    internal const string Reason = "attempt_not_started_before_interruption";

    internal static AttemptFacts Facts(RunAttemptPlan plan) => new(Guid.Parse(plan.RunId), Guid.Parse(plan.AttemptId), checked((int)plan.AttemptOrdinal),
        EngineOutcome.NotStarted, BusinessOutcome.Unverified, LifecycleOutcome.Blocked, RecoveryOutcome.NotRequired, Reason);

    internal static ExecutionOutcomes Outcomes() => new()
        { EngineOutcome = "notStarted", BusinessOutcome = "unverified", LifecycleOutcome = "blocked", RecoveryOutcome = "notRequired" };

    internal static bool Recorded(JsonElement root) => root.GetProperty("receipts").EnumerateArray().Any(IsExact);

    internal static bool IsExact(JsonElement receipt) => receipt.GetProperty("stepId").GetString() == "verify-no-effect" &&
        receipt.GetProperty("effect").GetString() == "notExecuted" && receipt.GetProperty("detailCode").GetString() == Reason &&
        receipt.TryGetProperty("outcomes", out var outcomes) && JsonElement.DeepEquals(outcomes, JsonSerializer.SerializeToElement(Outcomes()));
}
