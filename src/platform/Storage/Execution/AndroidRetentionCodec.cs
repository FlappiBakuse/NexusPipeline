using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Execution;

namespace NexusPipeline.Platform.Storage.Execution;

// The retention document is bound to the attempt that launched the emulator, so its participant lives on the
// attempt journal; every reader that enumerates attempt participants has to recognize the format.
internal sealed record AndroidRetentionIntent(string Format, int Version, ExecutionTargetBinding? Binding, string Stage,
    BlobReference? Artifact);

internal static class AndroidRetentionCodec
{
    internal const string Format = "nexus.execution.android-retention";
    internal const string Prepared = "prepared";
    internal const string Retained = "retained";
    internal const int MaximumIntentsPerBinding = 4;

    internal static bool IsParticipant(JsonElement participant) =>
        participant.GetProperty("identity").TryGetProperty("format", out var format) && format.GetString() == Format;

    internal static ImmutableArray<AndroidRetentionIntent> Read(JsonElement journal, ExecutionPlan total, string totalSha256)
    {
        journal = ExecutionInvocationCodec.Current(journal);
        var result = ImmutableArray.CreateBuilder<AndroidRetentionIntent>();
        foreach (var participant in journal.GetProperty("participants").EnumerateArray().Where(IsParticipant))
        {
            var decoded = ExecutionParticipantText.Read<AndroidRetentionIntent>(participant.GetProperty("identity"),
                StepResultCodec.Options, Invalid);
            var value = decoded.Value;
            var binding = value.Binding ?? throw Invalid();
            RequireBinding(journal, total, totalSha256, participant, binding);
            if (value.Artifact is not { } artifact) throw Invalid();
            StepEvidenceCodec.Plan(artifact);
            var prior = result.Where(item => item.Binding == binding).ToArray();
            if (prior.Length >= MaximumIntentsPerBinding || !decoded.MatchesOriginal || value.Format != Format ||
                value.Version != 1 || value.Stage is not (Prepared or Retained) ||
                (prior.Length == 0 ? value.Stage != Prepared : value.Stage == Retained && prior[^1].Stage != Prepared))
                throw Invalid();
            result.Add(value);
        }
        return result.ToImmutable();
    }

    internal static void RequireBinding(JsonElement journal, ExecutionPlan total, string totalSha256,
        JsonElement participant, ExecutionTargetBinding binding)
    {
        var input = total.Steps.SingleOrDefault(step => step.StepId == binding.StepId) ?? throw Invalid();
        if (input.Target?.Android?.Emulator is null || binding.InstanceId.ToString("D") != total.InstanceId ||
            binding.ParentJournalId != journal.GetProperty("journalId").GetGuid() ||
            binding.ParentOperationId != journal.GetProperty("operationId").GetGuid() ||
            binding.AttemptId != journal.GetProperty("attemptId").GetGuid() || binding.TotalPlanSha256 != totalSha256 ||
            participant.GetProperty("kind").GetString() != "process" ||
            participant.GetProperty("resourceKey").GetString() != "automation:" + input.ScriptId) throw Invalid();
    }

    internal static JsonObject Participant(AndroidRetentionIntent intent, string scriptId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "process",
        ["resourceKey"] = "automation:" + scriptId,
        ["identity"] = JsonSerializer.SerializeToNode(intent, StepResultCodec.Options)
    };

    internal static JsonException Invalid() => new("execution_emulator_retention_participant_unbound");
}
