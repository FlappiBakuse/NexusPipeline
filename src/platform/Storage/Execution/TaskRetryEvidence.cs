using System.Collections.Immutable;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record ExecutionTaskRetry(StepEvidenceBinding PreviousBinding, BlobReference PreviousRaw,
    BlobReference PreviousObservation, AdapterRetryTasks Selection);

internal static class TaskRetryEvidence
{
    internal static bool TryCapture(AttemptWriterState state, string stepId, ImmutableBlobStore blobs,
        out ExecutionTaskRetry? retry)
    {
        retry = null;
        var step = state.Total.Steps.Single(step => step.StepId == stepId);
        if (step.Adapter is null) return true;
        var result = StepResultCodec.Read(state.Latest.Root, state.Total).Single(item => item.Binding.StepId == stepId);
        if (!result.IsCollected) throw ExecutionInvocationCodec.Invalid();
        var evidence = StepEvidenceCodec.Read(blobs, result.Binding, result.Latest.Artifact);
        if (evidence.TaskAdapter is null || evidence.RawOutput is null) return false;
        var observed = AdapterObservationCodec.Read(blobs, result.Binding, evidence.RawOutput, step, evidence.TaskAdapter);
        AdapterRetryTasks selection;
        try { selection = AdapterRetryPolicy.Select(Tasks(step), observed.Observations, observed.ProposedRetry); }
        catch (ArgumentException) { return false; }
        if (selection.Targets.IsEmpty) return false;
        retry = new(result.Binding, evidence.RawOutput, evidence.TaskAdapter, selection);
        return true;
    }

    internal static ExecutionTaskRetry? ForJob(RuntimeInstance instance, ContractReader contracts,
        ExecutionPlan total, StepEvidenceBinding binding)
    {
        var latest = new JournalStore(instance, contracts).Read(binding.ParentJournalId);
        var view = ExecutionInvocationCodec.ForJob(latest.Root, total, binding.JobName);
        return ExecutionInvocationCodec.Read(view, total).LastOrDefault(item => item.StepId == binding.StepId)?.TaskRetry;
    }

    internal static void RequireInvocation(RuntimeInstance instance, ContractReader contracts, ExecutionPlan total,
        ExecutionAdapterObservation observation)
    {
        if (!Same(ForJob(instance, contracts, total, observation.Binding), observation.Retry)) throw ExecutionInvocationCodec.Invalid();
    }

    internal static ImmutableArray<AdapterTask> Tasks(ExecutionPlanStep step) =>
        step.Tasks.Select(TaskAdapterProtocol.ReadTask).ToImmutableArray();

    internal static string[] InvocationIds(ExecutionPlanStep step, ExecutionTaskRetry? retry) =>
        step.Tasks.Where(task => task.IsSelected && (retry is null ||
            retry.Selection.SelectedIds.Contains(task.TaskId, StringComparer.Ordinal) ||
            task.WorkflowRole == "cleanup" && task.Projection is null && task.RetrySafety == "safe"))
            .Select(task => task.TaskId).ToArray();

    internal static TaskDefinitionsRepetitionOffset[]? RepetitionOffsets(ImmutableBlobStore blobs, ExecutionPlanStep step, ExecutionTaskRetry? retry)
    {
        if (retry is null) return null;
        var previous = AdapterObservationCodec.Read(blobs, retry.PreviousBinding, retry.PreviousRaw, step, retry.PreviousObservation);
        var offsets = AdapterRepetitionPolicy.Offsets(Tasks(step), previous.Observations, retry.Selection);
        return offsets.IsEmpty ? null : offsets.Select(item => new TaskDefinitionsRepetitionOffset
            { TaskId = item.TaskId, CompletedRepetitions = item.CompletedRepetitions }).ToArray();
    }

    internal static ImmutableArray<string> Selected(ExecutionPlanStep step, RunTaskRule rule, ExecutionTaskRetry? retry) =>
        retry is null ? ExecutionTaskProjection.InitialIds(step, rule).ToImmutableArray() :
            retry.Selection.SelectedIds.Where(id => rule.Declaration.AuthorizedIds.Contains(id, StringComparer.Ordinal)).ToImmutableArray();

    internal static bool Same<T>(T left, T right) =>
        JsonElement.DeepEquals(JsonSerializer.SerializeToElement(left), JsonSerializer.SerializeToElement(right));
}
