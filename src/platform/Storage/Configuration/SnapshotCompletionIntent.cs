using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Storage.Configuration;

internal sealed record SnapshotCompletionIntent(string Format, int Version, Guid ParentOperationId,
    Guid ParentJournalId, string ParentPlanDigest, Guid SessionId, Guid CompletionOperationId, string SubjectId,
    string Terminal, long GroupRevision, string EvidenceDigest, ImmutableArray<string> SlotKeys);

internal static class SnapshotIntentCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    internal const string Format = "nexus.configuration.snapshot-completion";
    internal const string Step = "prepare-snapshot-completion";
    internal const string Reason = "configuration_snapshot_completion_pending";

    internal static IReadOnlyList<SnapshotCompletionIntent> Read(JsonElement journal)
    {
        var result = new List<SnapshotCompletionIntent>();
        foreach (var participant in journal.GetProperty("participants").EnumerateArray())
        {
            if (WriterIntentCodec.IsParticipant(participant) || DispatchIntentCodec.IsParticipant(participant) ||
                StepResultCodec.IsParticipant(participant) || StepResultCodec.IsUndispatchedParticipant(participant) ||
                AttemptResultCodec.IsParticipant(participant) || HookResultCodec.IsParticipant(participant) ||
                RetryHookCodec.IsParticipant(participant) || BindingSuppressionCodec.IsParticipant(participant) || TargetJobCodec.IsParticipant(participant) ||
                PcStageCodec.IsParticipant(participant) || AndroidControlCodec.IsParticipant(participant) ||
                AndroidStartupCodec.IsParticipant(participant) || AndroidCollectedCodec.IsParticipant(participant) ||
                AndroidRetentionCodec.IsParticipant(participant) ||
                ProviderTerminalCodec.IsParticipant(participant) || ProviderLauncherCodec.IsParticipant(participant) ||
                ExecutionCaptureCodec.IsParticipant(participant) || PreviewDomainCodec.IsParticipant(participant) ||
                BindingSkipCodec.IsParticipant(participant) || ExecutionInvocationCodec.IsParticipant(participant) ||
                BindingTimeoutCodec.IsParticipant(participant)) continue;
            var identity = participant.GetProperty("identity");
            if (!identity.TryGetProperty("format", out var format) || format.GetString() != Format) throw Invalid();
            var item = identity.Deserialize<SnapshotCompletionIntent>(Options) ?? throw Invalid();
            if (!JsonElement.DeepEquals(identity, JsonSerializer.SerializeToElement(item, Options)) || item.Format != Format || item.Version != 1 ||
                item.ParentOperationId == Guid.Empty || item.ParentJournalId == Guid.Empty || item.SessionId == Guid.Empty ||
                item.CompletionOperationId == Guid.Empty || item.ParentOperationId == item.SessionId || item.ParentOperationId == item.CompletionOperationId ||
                item.SessionId == item.CompletionOperationId || item.ParentOperationId != journal.GetProperty("operationId").GetGuid() ||
                item.ParentJournalId != journal.GetProperty("journalId").GetGuid() || item.ParentPlanDigest != journal.GetProperty("planDigest").GetString() ||
                !FileTreePolicy.Digest(item.EvidenceDigest) || item.Terminal != "interrupted" || item.GroupRevision is < 1 or > 9007199254740990 ||
                string.IsNullOrWhiteSpace(item.SubjectId) || item.SubjectId.Length > 256 || item.SubjectId.Any(char.IsControl) ||
                item.SlotKeys.IsDefaultOrEmpty || item.SlotKeys.Length > 32 || item.SlotKeys.Distinct(StringComparer.Ordinal).Count() != item.SlotKeys.Length ||
                !item.SlotKeys.SequenceEqual(item.SlotKeys.Order(StringComparer.Ordinal)) ||
                participant.GetProperty("kind").GetString() != "database" || participant.GetProperty("participantId").GetGuid() != item.SessionId ||
                participant.GetProperty("resourceKey").GetString() != item.SlotKeys[0] ||
                item.SlotKeys.Any(key => !journal.GetProperty("resourceKeys").EnumerateArray().Any(resource => resource.GetString() == key))) throw Invalid();
            if (result.Any(prior => prior.SessionId == item.SessionId || prior.CompletionOperationId == item.CompletionOperationId)) throw Invalid();
            if (journal.GetProperty("receipts").EnumerateArray().Count(receipt => IsReceipt(receipt, item)) != 1) throw Invalid();
            result.Add(item);
        }
        return result.AsReadOnly();
    }

    internal static bool IsReceipt(JsonElement receipt, SnapshotCompletionIntent item) =>
        receipt.GetProperty("receiptId").GetGuid() == item.CompletionOperationId && receipt.GetProperty("stepId").GetString() == Step &&
        receipt.GetProperty("effect").GetString() == "completed" && receipt.GetProperty("detailCode").GetString() == Reason &&
        !receipt.TryGetProperty("outcomes", out _);

    internal static JsonObject Participant(SnapshotCompletionIntent item) => new()
    {
        ["participantId"] = item.SessionId.ToString("D"), ["kind"] = "database", ["resourceKey"] = item.SlotKeys[0],
        ["identity"] = JsonSerializer.SerializeToNode(item, Options)
    };

    private static JsonException Invalid() => new("configuration_snapshot_completion_intent_unrecognized");
}
