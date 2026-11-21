using System.Collections.Immutable;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class DailyAdapterEvidence
{
    internal static bool Applies(ExecutionPlanStep step) => step is { Adapter: not null, Provider: null, Purpose: not "initialization" } &&
        step.ExtensionId is "baah" or "bettergi" or "maaend" or "maas" or "march7th" or "oknte" or "okww" or "zzzonedragon";

    internal static (BusinessOutcome Outcome, string ReasonCode, ImmutableArray<AdapterTaskObservation> Tasks) Decide(
        ExecutionPlanStep step, ExecutionAdapterObservation observed, RawOutputManifest raw, EngineOutcome engine, bool naturallyExited)
    {
        var observations = observed.Observations.Select(value => Classify(value, raw)).ToImmutableArray();
        var launcher = Path.GetFileNameWithoutExtension(step.ExecutablePath).Contains("launcher", StringComparison.OrdinalIgnoreCase);
        var verifiedTasks = observations.Any(value => value.State == AdapterObservationState.Succeeded &&
            value.CompletionKind is null or AdapterCompletion.Completed && step.Tasks.Any(task => task.TaskId == value.TaskId && task.WorkflowRole == "business"));
        var directProgramOutput = !launcher && (step.LogTargets.Count == 0 ? raw.ObservedBytes > 0 : raw.Parts.Any(part => part.FileSourceId is not null));
        var result = AdapterBusinessPolicy.DecideDaily(step.Tasks.Select(TaskAdapterProtocol.ReadTask).ToImmutableArray(), observations,
            engine, naturallyExited && raw.IsComplete && (verifiedTasks || directProgramOutput),
            step.Tasks.Any(task => IsUnselectedOpaque(step.ExtensionId, task)));
        return result.Outcome == BusinessOutcome.Unverified && engine == EngineOutcome.Succeeded && launcher && !verifiedTasks
            ? (result.Outcome, "adapter_business_launcher_unverified", result.Tasks) : result;
    }

    internal static bool IsUnselectedOpaque(string? extensionId, TaskDefinitionsTask task) => task.IsEnabled && !task.IsSelected && (extensionId switch
    {
        "baah" or "maaend" => task.Projection is null && task.WorkflowRole == "business",
        "oknte" => task.TaskId.StartsWith("oknte:unknown/", StringComparison.Ordinal),
        "okww" or "zzzonedragon" or "march7th" => task.TitleLocales is null && task.WorkflowRole == "business",
        _ => false
    });

    internal static ImmutableArray<AdapterTaskObservation> Project(ImmutableBlobStore blobs, ExecutionPlanStep step, StepEvidenceDocument document)
    {
        if (document.TaskAdapter is not { } adapter || document.RawOutput is not { } output) return [];
        var observed = AdapterObservationCodec.Read(blobs, document.Binding, output, step, adapter);
        if (!document.DailyAdapterTerminal) return observed.Observations;
        var prior = document.PreviousObservation is { } reference ? StepEvidenceCodec.Read(blobs, document.Binding, reference) : null;
        return Decide(step, observed, RawOutputCodec.Read(blobs, document.Binding, output), document.Engine,
            prior?.Engine == EngineOutcome.Succeeded).Tasks;
    }

    private static AdapterTaskObservation Classify(AdapterTaskObservation value, RawOutputManifest raw)
    {
        if (value.State != AdapterObservationState.Unknown || value.CompletionKind is not null || value.ReasonCode == AdapterBusinessPolicy.OpaqueTask)
            return value;
        var reason = value.ReasonCode switch
        {
            "adapter_output_incomplete" or "bettergi_log_sequence_discontinuous" => "adapter_log_gap",
            "adapter_observation_unavailable" or "bettergi_upstream_process_identity_required" => value.ReasonCode,
            "okww_daily_aborted_child_unproven" => "adapter_task_not_executed",
            "bettergi_scope_terminal_missing" or "bettergi_outer_end_without_task_terminal" or "adapter_task_missing_terminal" => "adapter_task_missing_terminal",
            "bettergi_task_terminal_missing" => "adapter_task_missing_logs",
            _ => raw.ObservedBytes == 0 || value.ReasonCode == "adapter_task_unobserved" ? "adapter_task_missing_logs" : "adapter_task_missing_terminal"
        };
        return value with { ReasonCode = reason, CompletionKind = reason == "adapter_task_not_executed" ? AdapterCompletion.NotExecuted : null };
    }
}
