using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Execution;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record ExecutionBindingSuppression(string Format, int Version, string StepId, ImmutableArray<string> Roles,
    string ReasonCode, Guid? DispatchOperationId);

internal static class BindingSuppressionCodec
{
    internal const string Format = "nexus.execution.binding-suppression";

    internal static bool IsParticipant(JsonElement participant)
    {
        var identity = participant.GetProperty("identity");
        if (!identity.TryGetProperty("format", out var format) || format.GetString() != Format) return false;
        _ = Decode(participant); return true;
    }

    internal static ImmutableArray<ExecutionBindingSuppression> Read(JsonElement journal, NexusPipeline.Platform.Serialization.Protocol.ExecutionPlan total)
    {
        journal = ExecutionInvocationCodec.Current(journal);
        var jobs = WriterIntentCodec.ReadJobs(journal, total);
        var hooks = HookResultCodec.Read(journal, total);
        var close = DispatchIntentCodec.Read(journal, total);
        var result = ImmutableArray.CreateBuilder<ExecutionBindingSuppression>();
        foreach (var participant in journal.GetProperty("participants").EnumerateArray().Where(IsParticipant))
        {
            var item = Decode(participant);
            var step = total.Steps.SingleOrDefault(step => step.StepId == item.StepId) ?? throw Invalid();
            var current = jobs.Where(job => job.StepId == item.StepId).ToArray();
            var absent = ExecutionBindingRoles.Required(step).Where(role => current.All(job => job.Role != role) && !RetryHookCodec.ResolvedRoles(journal, total, item.StepId).Contains(role)).ToArray();
            if (current.Length == 0 || current.Any(job => job.ClosedElapsedMilliseconds is null) || !absent.SequenceEqual(item.Roles) ||
                result.Any(prior => prior.StepId == item.StepId) || participant.GetProperty("resourceKey").GetString() != "automation:" + step.ScriptId) throw Invalid();
            if (item.DispatchOperationId is { } operation)
            {
                if (close is null || close.CancellationOperationId != operation || close.ReasonCode != item.ReasonCode) throw Invalid();
            }
            else if (item.ReasonCode == BindingTimeoutCodec.Reason)
            {
                if (!BindingTimeoutCodec.Read(journal, total).Any(timeout => timeout.StepId == item.StepId)) throw Invalid();
            }
            else if (item.ReasonCode == "execution_pre_hook_failed")
            {
                if (current.Any(job => job.Role != "preHook") ||
                    !hooks.Any(hook => hook.StepId == item.StepId && hook.Role == "preHook" && hook.Engine == EngineOutcome.Failed)) throw Invalid();
            }
            else
            {
                var main = StepResultCodec.Read(journal, total).SingleOrDefault(result => result.Binding.StepId == item.StepId);
                var stages = PcStageCodec.ReadStructure(journal, total);
                var stage = stages.SingleOrDefault(stage => stage.Binding.StepId == item.StepId);
                if (stage is null || stage.Outcome is not (TargetExecutionOutcome.LaunchFailed or TargetExecutionOutcome.ReadinessTimedOut) ||
                    stage.ReasonCode != item.ReasonCode || main is not { IsCollected: true } ||
                    main.Latest.Engine != EngineOutcome.NotStarted || main.Latest.Business != BusinessOutcome.Unverified) throw Invalid();
            }
            result.Add(item);
        }
        return result.ToImmutable();
    }

    internal static JsonObject Participant(ExecutionBindingSuppression item, string scriptId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "database", ["resourceKey"] = "automation:" + scriptId,
        ["identity"] = JsonSerializer.SerializeToNode(item, StepResultCodec.Options)
    };

    private static ExecutionBindingSuppression Decode(JsonElement participant)
    {
        var identity = participant.GetProperty("identity");
        var item = identity.Deserialize<ExecutionBindingSuppression>(StepResultCodec.Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(identity, JsonSerializer.SerializeToElement(item, StepResultCodec.Options)) ||
            item.Format != Format || item.Version != 1 || string.IsNullOrWhiteSpace(item.StepId) || item.StepId.Length > 120 ||
            item.Roles.IsDefaultOrEmpty || item.Roles.Length > 4 || item.Roles.Distinct(StringComparer.Ordinal).Count() != item.Roles.Length ||
            item.Roles.Any(role => role is not ("preHook" or "process" or "judgement" or "postHook")) ||
            item.DispatchOperationId == Guid.Empty || (item.DispatchOperationId is null) != (item.ReasonCode is BindingTimeoutCodec.Reason or "execution_pre_hook_failed" or "execution_target_launch_failed" or "execution_target_readiness_timeout") ||
            item.DispatchOperationId is not null && item.ReasonCode is not (DispatchIntentCodec.Reason or DispatchIntentCodec.CompletionReason or
                DispatchIntentCodec.InterruptionReason or DispatchIntentCodec.BudgetReason or DispatchIntentCodec.TargetReason) ||
            participant.GetProperty("kind").GetString() != "database") throw Invalid();
        return item;
    }

    private static JsonException Invalid() => new("execution_binding_suppression_unrecognized");
}
