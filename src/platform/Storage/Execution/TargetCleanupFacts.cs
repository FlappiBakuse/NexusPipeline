using NexusPipeline.Core.Execution;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class TargetCleanupFacts
{
    internal static ExecutionTargetCleanup Read(AttemptWriterState state, string stepId) =>
        Read(state.Total, state.Latest.Root, state.Attempt.ExecutionPlan.Sha256, stepId);

    internal static ExecutionTargetCleanup ReadAtAndroidCollection(ExecutionPlan total, JsonElement journal, string totalSha256, string stepId)
    {
        var participants = journal.GetProperty("participants").EnumerateArray().ToArray();
        var prefix = participants.TakeWhile(item => !AndroidCollectedCodec.IsParticipant(item) ||
            item.GetProperty("identity").GetProperty("binding").GetProperty("stepId").GetString() != stepId).ToArray();
        if (prefix.Length == participants.Length) return Read(total, journal, totalSha256, stepId);
        // Append-only participants preserve the facts that authorized cleanup before any later close.
        var context = JsonNode.Parse(journal.GetRawText())!.AsObject();
        context["participants"] = new JsonArray(prefix.Select(item => JsonNode.Parse(item.GetRawText())).ToArray());
        return Read(total, JsonSerializer.SerializeToElement(context), totalSha256, stepId);
    }

    internal static ExecutionTargetCleanup Read(ExecutionPlan total, JsonElement journal, string totalSha256, string stepId)
    {
        var step = total.Steps.Single(item => item.StepId == stepId);
        if (step.Target is not { } target || target.Kind == "pc" && (!target.LaunchByHost || target.Pc is null || target.Android is not null) ||
            target.Kind == "android" && (target.Android is null || target.Pc is not null) || target.Kind is not ("pc" or "android"))
            throw new IOException("execution_target_definition_required");
        var result = StepResultCodec.Read(journal, total).SingleOrDefault(item => item.Binding.StepId == stepId);
        var main = result is null ? new ExecutionStepFact(stepId, EngineOutcome.NotStarted, BusinessOutcome.Unverified, "execution_primary_not_dispatched") :
            new(stepId, result.Latest.Engine, result.Latest.Business, result.Latest.ReasonCode);
        var localStop = result?.Latest.ReasonCode switch
        {
            "execution_business_failed" => StepStopCause.BusinessFailed,
            "execution_step_stall_timeout" => StepStopCause.StallTimeout,
            "execution_step_total_timeout" => StepStopCause.StepTotalTimeout,
            _ => StepStopCause.Continue
        };
        var stop = DispatchIntentCodec.Read(journal, total)?.ReasonCode switch
        {
            null or DispatchIntentCodec.CompletionReason => ExecutionStopReason.Completed,
            DispatchIntentCodec.Reason => ExecutionStopReason.Cancelled,
            DispatchIntentCodec.BudgetReason => ExecutionStopReason.TotalBudgetExhausted,
            DispatchIntentCodec.InterruptionReason or DispatchIntentCodec.TargetReason => ExecutionStopReason.Interrupted,
            _ => throw new IOException("execution_target_cleanup_close_unrecognized")
        };
        var outcome = target.Kind == "pc" ? PcStageCodec.Read(journal, total, totalSha256)
            .SingleOrDefault(item => item.Binding.StepId == stepId)?.Outcome :
            AndroidStartupCodec.Read(journal, total, totalSha256).SingleOrDefault(item => item.Binding.StepId == stepId)?.Outcome;
        var stage = outcome switch
        {
            TargetExecutionOutcome.LaunchFailed => TargetStageOutcome.LaunchFailed,
            TargetExecutionOutcome.ReadinessTimedOut => TargetStageOutcome.ReadinessTimedOut,
            TargetExecutionOutcome.OwnedReady or TargetExecutionOutcome.ExternalReady or
                TargetExecutionOutcome.ControlledReady => TargetStageOutcome.Ready,
            _ => TargetStageOutcome.NotAttempted
        };
        return TargetLifecyclePolicy.Cleanup(new(target.Kind == "pc" ? ExecutionTargetKind.Pc : ExecutionTargetKind.Android,
            target.LaunchByHost, target.ForceCloseAtEnd), target.Kind == "pc" ? ExecutionTargetAuthority.OwnedPcProcess :
            ExecutionTargetAuthority.ExplicitAndroidControl, main, stop, ExecutionRunContinuation.Final, localStop, stage);
    }
}
