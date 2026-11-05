using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record AttemptResultIntent(string Format, int Version, Guid ResultId, Guid ParentJournalId, Guid ParentOperationId,
    string PlanDigest, string Mode, AttemptFacts Facts);

internal static class AttemptResultCodec
{
    internal const string Format = "nexus.execution.attempt-result";
    internal const string Step = "complete-attempt";
    internal static bool IsParticipant(JsonElement participant)
    {
        var identity = participant.GetProperty("identity");
        if (!identity.TryGetProperty("format", out var format) || format.GetString() != Format) return false;
        _ = Decode(participant); return true;
    }

    internal static AttemptResultIntent? Read(JsonElement journal, RunAttemptPlan plan)
    {
        AttemptResultIntent? result = null;
        foreach (var participant in journal.GetProperty("participants").EnumerateArray().Where(IsParticipant))
        {
            var item = Decode(participant); var facts = item.Facts;
            if (result is not null || item.ParentJournalId != Guid.Parse(plan.JournalId) || item.ParentOperationId != Guid.Parse(plan.OperationId) ||
                item.PlanDigest != journal.GetProperty("planDigest").GetString() || facts.RunId != Guid.Parse(plan.RunId) ||
                facts.AttemptId != Guid.Parse(plan.AttemptId) || facts.Ordinal != plan.AttemptOrdinal ||
                !journal.GetProperty("resourceKeys").EnumerateArray().Any(key => key.GetString() == participant.GetProperty("resourceKey").GetString()) ||
                journal.GetProperty("receipts").EnumerateArray().Count(receipt => IsReceipt(receipt, item)) != 1) throw Invalid();
            result = item;
        }
        return result;
    }

    internal static bool IsReceipt(JsonElement receipt, AttemptResultIntent result) => receipt.GetProperty("receiptId").GetGuid() == result.ResultId &&
        receipt.GetProperty("stepId").GetString() == Step && receipt.GetProperty("effect").GetString() == (result.Mode == "executed" ? "completed" : "notExecuted") &&
        receipt.GetProperty("detailCode").GetString() == result.Facts.ReasonCode && receipt.TryGetProperty("outcomes", out var outcomes) &&
        JsonElement.DeepEquals(outcomes, JsonSerializer.SerializeToElement(Outcomes(result.Facts)));

    internal static JsonObject Participant(AttemptResultIntent result, string resource) => new()
    {
        ["participantId"] = result.ResultId.ToString("D"), ["kind"] = "database", ["resourceKey"] = resource,
        ["identity"] = JsonSerializer.SerializeToNode(result, StepResultCodec.Options)
    };

    internal static JsonObject Receipt(AttemptResultIntent result, string recordedAt) => new()
    {
        ["receiptId"] = result.ResultId.ToString("D"), ["recordedAt"] = recordedAt, ["stepId"] = Step,
        ["effect"] = result.Mode == "executed" ? "completed" : "notExecuted", ["detailCode"] = result.Facts.ReasonCode,
        ["outcomes"] = JsonSerializer.SerializeToNode(Outcomes(result.Facts))
    };

    internal static AttemptReceipt Receipt(RunAttemptPlan plan, JournalDocument document, AttemptFacts facts) => new(document.JournalId,
        Guid.Parse(plan.OperationId), document.PlanDigest, Convert.ToHexStringLower(SHA256.HashData(document.Export())), facts);

    private static ExecutionOutcomes Outcomes(AttemptFacts facts) => new()
    {
        EngineOutcome = Lower(facts.Engine), BusinessOutcome = Lower(facts.Business), LifecycleOutcome = Lower(facts.Lifecycle), RecoveryOutcome = Lower(facts.Recovery)
    };
    private static string Lower<T>(T value) where T : Enum
    {
        var text = value.ToString(); return char.ToLowerInvariant(text[0]) + text[1..];
    }
    private static AttemptResultIntent Decode(JsonElement participant)
    {
        var identity = participant.GetProperty("identity"); var item = identity.Deserialize<AttemptResultIntent>(StepResultCodec.Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(identity, JsonSerializer.SerializeToElement(item, StepResultCodec.Options)) || item.Format != Format || item.Version != 1 ||
            item.ResultId == Guid.Empty || item.ParentJournalId == Guid.Empty || item.ParentOperationId == Guid.Empty || !FileTreePolicy.Digest(item.PlanDigest) ||
            participant.GetProperty("participantId").GetGuid() != item.ResultId || participant.GetProperty("kind").GetString() != "database" ||
            item.Mode is not ("executed" or "noEffectCancellation" or "noEffectStop") || item.Facts is not { } facts ||
            !Enum.IsDefined(facts.Engine) || !Enum.IsDefined(facts.Business) || !Enum.IsDefined(facts.Lifecycle) || !Enum.IsDefined(facts.Recovery) ||
            facts.Engine == EngineOutcome.Running || facts.Lifecycle is not (LifecycleOutcome.Completed or LifecycleOutcome.Cancelled or LifecycleOutcome.Interrupted or LifecycleOutcome.Blocked or LifecycleOutcome.Skipped) ||
            facts.Recovery != (item.Mode == "executed" ? RecoveryOutcome.Restored : RecoveryOutcome.NotRequired) ||
            string.IsNullOrWhiteSpace(facts.ReasonCode) || facts.ReasonCode.Length > 120 ||
            item.Mode == "noEffectCancellation" && (facts.Engine != EngineOutcome.NotStarted || facts.Business != BusinessOutcome.Unverified ||
                facts.Lifecycle != LifecycleOutcome.Cancelled || facts.ReasonCode != DispatchIntentCodec.Reason) ||
            item.Mode == "noEffectStop" && (facts.Engine != EngineOutcome.NotStarted || facts.Business != BusinessOutcome.Unverified ||
                !(facts.Lifecycle == LifecycleOutcome.Interrupted && facts.ReasonCode is (DispatchIntentCodec.InterruptionReason or DispatchIntentCodec.TargetReason) ||
                  facts.Lifecycle == LifecycleOutcome.Blocked && facts.ReasonCode == DispatchIntentCodec.BudgetReason ||
                  facts.Lifecycle == LifecycleOutcome.Skipped && facts.ReasonCode == DispatchIntentCodec.CompletionReason)) ||
            item.Mode == "executed" && facts.Lifecycle == LifecycleOutcome.Blocked) throw Invalid();
        return item;
    }
    private static JsonException Invalid() => new("execution_attempt_result_unrecognized");
}
