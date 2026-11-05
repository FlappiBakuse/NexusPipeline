using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record AndroidStartupCommand(AndroidCommandRegistration Registration, BlobReference Observation);
internal sealed record AndroidStartupDocument(string Format, int Version, AndroidControlBinding Binding,
    TargetExecutionOutcome Outcome, string ReasonCode, ImmutableArray<AndroidStartupCommand> Commands);
internal sealed record AndroidStartupIntent(string Format, int Version, AndroidControlBinding Binding,
    TargetExecutionOutcome Outcome, string ReasonCode, ImmutableArray<TargetCommandFact> Commands, BlobReference Artifact);

internal static class AndroidStartupCodec
{
    internal const string Format = "nexus.execution.android-startup";

    internal static bool IsParticipant(JsonElement participant) => participant.GetProperty("identity")
        .TryGetProperty("format", out var value) && value.GetString() == Format;

    internal static ImmutableArray<AndroidStartupIntent> Read(JsonElement journal, ExecutionPlan total, string totalSha256)
    {
        journal = ExecutionInvocationCodec.Current(journal);
        var result = ImmutableArray.CreateBuilder<AndroidStartupIntent>();
        var registrations = AndroidControlCodec.Read(journal, total, totalSha256);
        foreach (var participant in journal.GetProperty("participants").EnumerateArray().Where(IsParticipant))
        {
            var identity = participant.GetProperty("identity");
            var decoded = ExecutionParticipantText.Read<AndroidStartupIntent>(identity, StepResultCodec.Options, Invalid);
            var value = decoded.Value;
            var binding = value.Binding ?? throw Invalid();
            RequireBinding(journal, total, totalSha256, participant, binding);
            var input = total.Steps.Single(step => step.StepId == binding.StepId);
            var prefix = registrations.Where(item => item.Binding.StepId == binding.StepId &&
                !AndroidControlAdmission.IsCleanup(item.Purpose)).ToArray();
            if (!decoded.MatchesOriginal ||
                value.Format != Format || value.Version != 1 || result.Any(item => item.Binding.StepId == binding.StepId) ||
                !Allowed(value.Outcome) || value.Commands.IsDefault || value.Commands.Length != prefix.Length ||
                !value.Commands.Select(item => item.CommandId).SequenceEqual(prefix.Select(item => item.Binding.TargetId)) ||
                value.Outcome == TargetExecutionOutcome.Skipped && (input.Target!.LaunchByHost || prefix.Length != 0) ||
                value.Outcome == TargetExecutionOutcome.ControlledReady && prefix.Length == 0 ||
                (value.Outcome == TargetExecutionOutcome.StoppedByRequest
                    ? DispatchIntentCodec.Read(journal, total)?.ReasonCode != value.ReasonCode
                    : value.ReasonCode != Reason(value.Outcome))) throw Invalid();
            RequireCommandFacts(binding.StepId, value.Commands);
            StepEvidenceCodec.Plan(value.Artifact); result.Add(value);
        }
        return result.ToImmutable();
    }

    internal static void RequireBinding(JsonElement journal, ExecutionPlan total, string totalSha256, JsonElement participant,
        AndroidControlBinding binding)
    {
        var input = total.Steps.SingleOrDefault(step => step.StepId == binding.StepId) ?? throw Invalid();
        if (input.Target?.Kind != "android" || binding.InstanceId.ToString("D") != total.InstanceId ||
            binding.ParentJournalId != journal.GetProperty("journalId").GetGuid() ||
            binding.ParentOperationId != journal.GetProperty("operationId").GetGuid() ||
            binding.AttemptId != journal.GetProperty("attemptId").GetGuid() || binding.TotalPlanSha256 != totalSha256 ||
            participant.GetProperty("kind").GetString() != "process" ||
            participant.GetProperty("resourceKey").GetString() != "automation:" + input.ScriptId) throw Invalid();
    }

    internal static void RequireCommandFacts(string stepId, ImmutableArray<TargetCommandFact> commands,
        TargetCleanupFact? cleanup = null)
    {
        if (commands.IsDefault) throw Invalid();
        _ = TargetFactsPolicy.Compose(new(stepId, EngineOutcome.NotStarted, BusinessOutcome.Unverified,
            "execution_primary_not_dispatched"), new(TargetExecutionOutcome.Skipped, "execution_android_validation"), commands, cleanup);
    }

    internal static string Reason(TargetExecutionOutcome outcome) => outcome switch
    {
        TargetExecutionOutcome.Skipped => "execution_android_launch_disabled",
        TargetExecutionOutcome.ControlledReady => "execution_android_controlled_ready",
        TargetExecutionOutcome.LaunchFailed => "execution_android_launch_failed",
        TargetExecutionOutcome.ReadinessTimedOut => "execution_android_readiness_timeout",
        TargetExecutionOutcome.ReadinessUnknown => "execution_android_readiness_unknown",
        TargetExecutionOutcome.Blocked => "execution_android_launch_blocked",
        _ => throw Invalid()
    };

    internal static bool Allowed(TargetExecutionOutcome outcome) => outcome is TargetExecutionOutcome.Skipped or
        TargetExecutionOutcome.ControlledReady or TargetExecutionOutcome.LaunchFailed or
        TargetExecutionOutcome.ReadinessTimedOut or TargetExecutionOutcome.ReadinessUnknown or
        TargetExecutionOutcome.Blocked or TargetExecutionOutcome.StoppedByRequest;

    internal static JsonObject Participant(AndroidStartupIntent item, string scriptId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "process", ["resourceKey"] = "automation:" + scriptId,
        ["identity"] = JsonSerializer.SerializeToNode(item, StepResultCodec.Options)
    };
    private static IOException Invalid() => new("execution_android_startup_unrecognized");
}
