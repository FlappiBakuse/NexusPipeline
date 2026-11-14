using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record PcStageBinding(Guid InstanceId, Guid ParentJournalId, Guid ParentOperationId,
    Guid AttemptId, string StepId, string TotalPlanSha256);
internal sealed record PcStageIntent(string Format, int Version, PcStageBinding Binding,
    TargetExecutionOutcome Outcome, string ReasonCode, BlobReference Artifact);
internal sealed record PcStageDocument(string Format, int Version, PcStageBinding Binding,
    TargetExecutionOutcome Outcome, string ReasonCode, string ReadinessMode, ImmutableArray<ProcessIdentity> ReadyMembers,
    ExecutionTargetBinding? TargetJob, BlobReference? TargetEvidence, long ElapsedMilliseconds);

internal static class PcStageCodec
{
    internal const string Format = "nexus.execution.pc-target-stage";

    internal static bool IsParticipant(JsonElement participant)
    {
        var identity = participant.GetProperty("identity");
        if (!identity.TryGetProperty("format", out var format) || format.GetString() != Format) return false;
        _ = Decode(participant); return true;
    }

    internal static ImmutableArray<PcStageIntent> Read(JsonElement journal, ExecutionPlan total, string totalSha256) => ReadCore(journal, total, totalSha256);

    internal static ImmutableArray<PcStageIntent> ReadStructure(JsonElement journal, ExecutionPlan total) => ReadCore(journal, total, null);

    private static ImmutableArray<PcStageIntent> ReadCore(JsonElement journal, ExecutionPlan total, string? totalSha256)
    {
        journal = ExecutionInvocationCodec.Current(journal);
        var values = ImmutableArray.CreateBuilder<PcStageIntent>();
        foreach (var participant in journal.GetProperty("participants").EnumerateArray().Where(IsParticipant))
        {
            var item = Decode(participant);
            var binding = item.Binding;
            var step = total.Steps.SingleOrDefault(step => step.StepId == binding.StepId) ?? throw Invalid();
            if (binding.InstanceId.ToString("D") != total.InstanceId || binding.ParentJournalId.ToString("D") != journal.GetProperty("journalId").GetString() ||
                binding.ParentOperationId.ToString("D") != journal.GetProperty("operationId").GetString() ||
                binding.AttemptId.ToString("D") != journal.GetProperty("attemptId").GetString() || totalSha256 is not null && binding.TotalPlanSha256 != totalSha256 ||
                step.Target is not { Kind: "pc", Pc: not null, Android: null } || values.Any(value => value.Binding.StepId == binding.StepId) ||
                participant.GetProperty("resourceKey").GetString() != "automation:" + step.ScriptId ||
                step.Target.LaunchByHost && item.Outcome == TargetExecutionOutcome.Skipped ||
                !step.Target.LaunchByHost && item.Outcome is not (TargetExecutionOutcome.Skipped or TargetExecutionOutcome.StoppedByRequest)) throw Invalid();
            values.Add(item);
        }
        return values.ToImmutable();
    }

    internal static string Reason(TargetExecutionOutcome outcome) => outcome switch
    {
        TargetExecutionOutcome.Skipped => "execution_target_launch_skipped",
        TargetExecutionOutcome.ExternalReady => "execution_target_external_ready",
        TargetExecutionOutcome.OwnedReady => "execution_target_owned_ready",
        TargetExecutionOutcome.LaunchFailed => "execution_target_launch_failed",
        TargetExecutionOutcome.ReadinessTimedOut => "execution_target_readiness_timeout",
        TargetExecutionOutcome.Blocked or TargetExecutionOutcome.ReadinessUnknown => "execution_target_readiness_unverified",
        _ => throw Invalid()
    };

    internal static JsonObject Participant(PcStageIntent item, string scriptId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "process", ["resourceKey"] = "automation:" + scriptId,
        ["identity"] = JsonSerializer.SerializeToNode(item, StepResultCodec.Options)
    };

    private static PcStageIntent Decode(JsonElement participant)
    {
        var json = participant.GetProperty("identity");
        var decoded = ExecutionParticipantText.Read<PcStageIntent>(json, StepResultCodec.Options, Invalid);
        var value = decoded.Value;
        if (!decoded.MatchesOriginal ||
            value.Format != Format || value.Version != 1 || value.Binding is null || value.Artifact is null ||
            value.Binding.InstanceId == Guid.Empty || value.Binding.ParentJournalId == Guid.Empty || value.Binding.ParentOperationId == Guid.Empty ||
            value.Binding.AttemptId == Guid.Empty || string.IsNullOrWhiteSpace(value.Binding.StepId) || !Enum.IsDefined(value.Outcome) || value.Outcome == TargetExecutionOutcome.UnverifiedDispatch ||
            !NexusPipeline.Core.Configuration.FileTreePolicy.Digest(value.Binding.TotalPlanSha256) ||
            (value.Outcome == TargetExecutionOutcome.StoppedByRequest
                ? value.ReasonCode is not (DispatchIntentCodec.Reason or DispatchIntentCodec.InterruptionReason or DispatchIntentCodec.BudgetReason or DispatchIntentCodec.TargetReason)
                : value.ReasonCode != Reason(value.Outcome)) || participant.GetProperty("kind").GetString() != "process") throw Invalid();
        StepEvidenceCodec.Plan(value.Artifact);
        return value;
    }

    private static IOException Invalid() => new("execution_target_stage_unrecognized");
}
