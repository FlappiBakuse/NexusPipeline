using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record DispatchCloseIntent(string Format, int Version, Guid ParentOperationId, Guid ParentJournalId,
    string ParentPlanDigest, Guid RunId, Guid AttemptId, Guid CancellationOperationId, string SubjectId, string ReasonCode);

internal static class DispatchIntentCodec
{
    internal const string Format = "nexus.execution.dispatch-close";
    internal const string OperationKind = "execution.cancel";
    internal const string Reason = "execution_cancel_requested";
    internal const string CompletionReason = "execution_completion_requested";
    internal const string InterruptionReason = "host_interrupted";
    internal const string BudgetReason = "execution_total_budget_exhausted";
    internal const string TargetReason = "execution_target_readiness_unverified";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static OperationIdentity Identity(Guid operationId, string subject, Guid parentJournalId, Guid parentOperationId, Guid runId, Guid attemptId,
        string reasonCode = Reason)
    {
        if (parentJournalId == Guid.Empty || parentOperationId == Guid.Empty || runId == Guid.Empty || attemptId == Guid.Empty || operationId == parentOperationId)
            throw new ArgumentException("execution_cancel_identity_invalid");
        var kind = Kind(reasonCode);
        var bytes = BoundedJson.Serialize(new { operationKind = kind, parentJournalId = parentJournalId.ToString("D"), parentOperationId = parentOperationId.ToString("D"),
            runId = runId.ToString("D"), attemptId = attemptId.ToString("D"), subject, reasonCode });
        return new(operationId, subject, Convert.ToHexStringLower(SHA256.HashData(bytes)), kind);
    }

    internal static string Kind(string reason) => reason switch
    {
        Reason => OperationKind,
        CompletionReason or InterruptionReason or BudgetReason or TargetReason => "execution.close",
        _ => throw new ArgumentException("execution_dispatch_reason_unrecognized")
    };

    internal static bool IsParticipant(JsonElement participant)
    {
        var identity = participant.GetProperty("identity");
        if (!identity.TryGetProperty("format", out var format) || format.GetString() != Format) return false;
        _ = Decode(participant); return true;
    }

    internal static DispatchCloseIntent? Read(JsonElement journal, ExecutionPlan total)
    {
        DispatchCloseIntent? result = null;
        foreach (var participant in journal.GetProperty("participants").EnumerateArray().Where(IsParticipant))
        {
            var item = Decode(participant);
            if (result is not null || item.ParentOperationId != journal.GetProperty("operationId").GetGuid() ||
                item.ParentJournalId != journal.GetProperty("journalId").GetGuid() || item.ParentPlanDigest != journal.GetProperty("planDigest").GetString() ||
                item.RunId != journal.GetProperty("runId").GetGuid() || item.AttemptId != journal.GetProperty("attemptId").GetGuid() ||
                participant.GetProperty("resourceKey").GetString() != "automation:" + total.Steps[0].ScriptId) throw Invalid();
            result = item;
        }
        return result;
    }

    internal static JsonObject Participant(DispatchCloseIntent item, string scriptId) => new()
    {
        ["participantId"] = item.CancellationOperationId.ToString("D"), ["kind"] = "database", ["resourceKey"] = "automation:" + scriptId,
        ["identity"] = JsonSerializer.SerializeToNode(item, Options)
    };

    private static DispatchCloseIntent Decode(JsonElement participant)
    {
        var identity = participant.GetProperty("identity");
        var item = identity.Deserialize<DispatchCloseIntent>(Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(identity, JsonSerializer.SerializeToElement(item, Options)) || item.Format != Format || item.Version != 1 ||
            item.ParentOperationId == Guid.Empty || item.ParentJournalId == Guid.Empty || item.RunId == Guid.Empty || item.AttemptId == Guid.Empty ||
            item.CancellationOperationId == Guid.Empty || item.CancellationOperationId == item.ParentOperationId ||
            !FileTreePolicy.Digest(item.ParentPlanDigest) || item.ReasonCode is not (Reason or CompletionReason or InterruptionReason or BudgetReason or TargetReason) ||
            string.IsNullOrWhiteSpace(item.SubjectId) || item.SubjectId.Length > 256 ||
            participant.GetProperty("participantId").GetGuid() != item.CancellationOperationId || participant.GetProperty("kind").GetString() != "database") throw Invalid();
        return item;
    }

    private static JsonException Invalid() => new("execution_dispatch_close_intent_unrecognized");
}
